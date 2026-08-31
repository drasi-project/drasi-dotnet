// Copyright 2026 The Drasi Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

//! JSON → [`SourceChange`] conversion matching the Python/Node host shape.

use std::sync::Arc;

use drasi_core::models::{
    Element, ElementMetadata, ElementPropertyMap, ElementReference, SourceChange,
};
use drasi_lib::sources::convert_json_to_element_value;
use serde_json::Value;

fn now_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

fn first<'a>(obj: &'a serde_json::Map<String, Value>, keys: &[&str]) -> Option<&'a Value> {
    keys.iter().find_map(|key| obj.get(*key))
}

fn labels_from(value: Option<&Value>) -> Arc<[Arc<str>]> {
    let labels: Vec<Arc<str>> = match value {
        Some(Value::Array(items)) => items
            .iter()
            .filter_map(|item| item.as_str())
            .map(Arc::from)
            .collect(),
        Some(Value::String(label)) => vec![Arc::from(label.as_str())],
        _ => Vec::new(),
    };
    Arc::from(labels)
}

fn properties_from(value: Option<&Value>) -> Result<ElementPropertyMap, String> {
    let mut map = ElementPropertyMap::new();
    match value {
        None | Some(Value::Null) => Ok(map),
        Some(Value::Object(obj)) => {
            for (key, item) in obj {
                map.insert(key.as_str(), convert_json_to_element_value(item));
            }
            Ok(map)
        }
        _ => Err("'properties' must be an object".into()),
    }
}

/// Expected JSON:
/// `{ "op": "insert"|"update"|"delete", "id": "...", "labels": [...], "properties": {...} }`
///
/// A relation when both `start_id`/`startId` and `end_id`/`endId` are present.
pub fn json_to_source_change(source_id: &str, input: &Value) -> Result<SourceChange, String> {
    let obj = input
        .as_object()
        .ok_or_else(|| "change must be a JSON object".to_string())?;

    let op = first(obj, &["op"])
        .and_then(Value::as_str)
        .ok_or_else(|| "change.op is required (insert|update|delete)".to_string())?
        .trim()
        .to_ascii_lowercase();

    let id = first(obj, &["id"])
        .and_then(Value::as_str)
        .ok_or_else(|| "change.id is required".to_string())?;

    let effective_from = first(obj, &["effective_from", "effectiveFrom"])
        .and_then(Value::as_u64)
        .unwrap_or_else(now_millis);

    let metadata = ElementMetadata {
        reference: ElementReference::new(source_id, id),
        labels: labels_from(first(obj, &["labels"])),
        effective_from,
    };

    if matches!(op.as_str(), "delete" | "remove") {
        return Ok(SourceChange::Delete { metadata });
    }

    let start = first(obj, &["start_id", "startId", "in_id", "inId"]).and_then(Value::as_str);
    let end = first(obj, &["end_id", "endId", "out_id", "outId"]).and_then(Value::as_str);

    let element = match (start, end) {
        (Some(start), Some(end)) => Element::Relation {
            metadata,
            in_node: ElementReference::new(source_id, start),
            out_node: ElementReference::new(source_id, end),
            properties: properties_from(first(obj, &["properties"]))?,
        },
        (None, None) => Element::Node {
            metadata,
            properties: properties_from(first(obj, &["properties"]))?,
        },
        _ => {
            return Err("a relation change requires both start_id/startId and end_id/endId".into())
        }
    };

    match op.as_str() {
        "insert" | "add" => Ok(SourceChange::Insert { element }),
        "update" => Ok(SourceChange::Update { element }),
        other => Err(format!(
            "unknown change.op '{other}' (expected insert|update|delete)"
        )),
    }
}

pub fn parse_string_list(json: &str, name: &str) -> Result<Vec<String>, String> {
    let value: Value =
        serde_json::from_str(json).map_err(|err| format!("{name} is not valid JSON: {err}"))?;
    match value {
        Value::Array(items) => items
            .into_iter()
            .map(|item| {
                item.as_str()
                    .map(str::to_string)
                    .ok_or_else(|| format!("each entry in {name} must be a string"))
            })
            .collect(),
        Value::String(one) => Ok(vec![one]),
        _ => Err(format!("{name} must be a JSON array of strings")),
    }
}
