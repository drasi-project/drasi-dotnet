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

//! JSON → engine types, matching the Python/Node host shape.

use std::sync::Arc;

use drasi_core::models::{
    Element, ElementMetadata, ElementPropertyMap, ElementReference, SourceChange,
    SourceMiddlewareConfig,
};
use drasi_lib::config::{QueryConfig, QueryJoinConfig, QueryJoinKeyConfig};
use drasi_lib::{ComponentStatus, DispatchMode, Query};
use serde::Deserialize;
use serde_json::Value;

use crate::error::{ErrorCode, FfiError, FfiResult};

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

fn properties_from(value: Option<&Value>) -> FfiResult<ElementPropertyMap> {
    match value {
        None | Some(Value::Null) => Ok(ElementPropertyMap::new()),
        // Preserve nested objects/arrays so query middleware (promote, unwind,
        // parse_json) can JSONPath into them. `convert_json_to_element_value`
        // stringifies objects, which is why Python uses `ElementPropertyMap::from`.
        Some(obj) if obj.is_object() => Ok(ElementPropertyMap::from(obj)),
        _ => Err(FfiError::new(
            ErrorCode::ConfigInvalid,
            "'properties' must be an object",
        )),
    }
}

/// Expected JSON:
/// `{ "op": "insert"|"update"|"delete", "id": "...", "labels": [...], "properties": {...} }`
///
/// A relation when both `start_id`/`startId` and `end_id`/`endId` are present.
pub fn json_to_source_change(source_id: &str, input: &Value) -> FfiResult<SourceChange> {
    let obj = input
        .as_object()
        .ok_or_else(|| FfiError::new(ErrorCode::ChangeNotObject, "change must be a JSON object"))?;

    let op = first(obj, &["op"])
        .and_then(Value::as_str)
        .ok_or_else(|| {
            FfiError::new(
                ErrorCode::ChangeOpRequired,
                "change.op is required (insert|update|delete)",
            )
        })?
        .trim()
        .to_ascii_lowercase();

    let id = first(obj, &["id"])
        .and_then(Value::as_str)
        .ok_or_else(|| FfiError::new(ErrorCode::ChangeIdRequired, "change.id is required"))?;

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
            return Err(FfiError::new(
                ErrorCode::RelationRequiresBothEnds,
                "a relation change requires both start_id/startId and end_id/endId",
            ))
        }
    };

    match op.as_str() {
        "insert" | "add" => Ok(SourceChange::Insert { element }),
        "update" => Ok(SourceChange::Update { element }),
        other => Err(FfiError::new(
            ErrorCode::UnknownChangeOp,
            format!("unknown change.op '{other}' (expected insert|update|delete)"),
        )),
    }
}

pub fn parse_string_list(json: &str, name: &str) -> FfiResult<Vec<String>> {
    let value: Value = serde_json::from_str(json)
        .map_err(|err| FfiError::config(format!("{name} is not valid JSON: {err}")))?;
    match value {
        Value::Array(items) => items
            .into_iter()
            .map(|item| {
                item.as_str().map(str::to_string).ok_or_else(|| {
                    FfiError::config(format!("each entry in {name} must be a string"))
                })
            })
            .collect(),
        Value::String(one) => Ok(vec![one]),
        _ => Err(FfiError::config(format!(
            "{name} must be a JSON array of strings"
        ))),
    }
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct QueryOptionsJson {
    pub language: Option<String>,
    pub auto_start: Option<bool>,
    pub enable_bootstrap: Option<bool>,
    pub bootstrap_timeout_seconds: Option<u64>,
    pub priority_queue_capacity: Option<usize>,
    pub dispatch_buffer_capacity: Option<usize>,
    pub outbox_capacity: Option<usize>,
    pub dispatch_mode: Option<String>,
    pub joins: Option<Vec<JoinJson>>,
    pub middleware: Option<Vec<MiddlewareJson>>,
}

#[derive(Debug, Deserialize)]
pub struct JoinJson {
    pub id: String,
    pub keys: Vec<JoinKeyJson>,
}

#[derive(Debug, Deserialize)]
pub struct JoinKeyJson {
    pub label: String,
    pub property: String,
}

#[derive(Debug, Deserialize)]
pub struct MiddlewareJson {
    pub name: String,
    pub kind: String,
    pub config: Option<serde_json::Map<String, Value>>,
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum SourceRefJson {
    Id(String),
    Spec {
        id: String,
        #[serde(default)]
        pipeline: Vec<String>,
    },
}

pub fn parse_source_subscriptions(json: &str) -> FfiResult<Vec<(String, Vec<String>)>> {
    let value: Value = serde_json::from_str(json)
        .map_err(|err| FfiError::config(format!("sources is not valid JSON: {err}")))?;
    let items = match value {
        Value::Array(items) => items,
        Value::String(one) => return Ok(vec![(one, Vec::new())]),
        _ => {
            return Err(FfiError::config(
                "sources must be a JSON array of ids or {id, pipeline} objects",
            ))
        }
    };

    items
        .into_iter()
        .map(|item| {
            let parsed: SourceRefJson = serde_json::from_value(item).map_err(|_| {
                FfiError::config(
                    "each source must be a string, or a mapping such as {\"id\":\"orders\",\"pipeline\":[\"unpack\"]}",
                )
            })?;
            Ok(match parsed {
                SourceRefJson::Id(id) => (id, Vec::new()),
                SourceRefJson::Spec { id, pipeline } => (id, pipeline),
            })
        })
        .collect()
}

pub fn parse_query_options(json: Option<&str>) -> FfiResult<QueryOptionsJson> {
    match json {
        None => Ok(QueryOptionsJson::default()),
        Some(raw) => serde_json::from_str(raw)
            .map_err(|err| FfiError::config(format!("query options are not valid JSON: {err}"))),
    }
}

fn parse_dispatch_mode(mode: &str) -> FfiResult<DispatchMode> {
    match mode.trim().to_ascii_lowercase().as_str() {
        "channel" => Ok(DispatchMode::Channel),
        "broadcast" => Ok(DispatchMode::Broadcast),
        other => Err(FfiError::config(format!(
            "unknown dispatch mode '{other}', expected 'channel' or 'broadcast'"
        ))),
    }
}

pub fn build_query(
    id: &str,
    query: &str,
    sources: &[(String, Vec<String>)],
    options: QueryOptionsJson,
) -> FfiResult<QueryConfig> {
    let language = options
        .language
        .as_deref()
        .unwrap_or("cypher")
        .trim()
        .to_ascii_lowercase();
    let mut builder = match language.as_str() {
        "cypher" => Query::cypher(id),
        "gql" => Query::gql(id),
        other => {
            return Err(FfiError::new(
                ErrorCode::UnknownQueryLanguage,
                format!("unknown query language '{other}', expected 'cypher' or 'gql'"),
            ))
        }
    };

    builder = builder.query(query);
    for (source, pipeline) in sources {
        builder = if pipeline.is_empty() {
            builder.from_source(source)
        } else {
            builder.from_source_with_pipeline(source, pipeline.clone())
        };
    }

    if let Some(middleware) = options.middleware {
        for declaration in middleware {
            builder = builder.with_middleware(SourceMiddlewareConfig {
                kind: Arc::from(declaration.kind.as_str()),
                name: Arc::from(declaration.name.as_str()),
                config: declaration.config.unwrap_or_default(),
            });
        }
    }

    if let Some(joins) = options.joins {
        let parsed: Vec<QueryJoinConfig> = joins
            .into_iter()
            .map(|join| {
                if join.keys.is_empty() {
                    return Err(FfiError::config("a join requires at least one key"));
                }
                Ok(QueryJoinConfig {
                    id: join.id,
                    keys: join
                        .keys
                        .into_iter()
                        .map(|key| QueryJoinKeyConfig {
                            label: key.label,
                            property: key.property,
                        })
                        .collect(),
                })
            })
            .collect::<FfiResult<_>>()?;
        builder = builder.with_joins(parsed);
    }

    if let Some(auto_start) = options.auto_start {
        builder = builder.auto_start(auto_start);
    }
    if let Some(enable) = options.enable_bootstrap {
        builder = builder.enable_bootstrap(enable);
    }
    if let Some(seconds) = options.bootstrap_timeout_seconds {
        builder = builder.with_bootstrap_timeout_secs(seconds);
    }
    if let Some(capacity) = options.priority_queue_capacity {
        builder = builder.with_priority_queue_capacity(capacity);
    }
    if let Some(capacity) = options.dispatch_buffer_capacity {
        builder = builder.with_dispatch_buffer_capacity(capacity);
    }
    if let Some(capacity) = options.outbox_capacity {
        builder = builder.with_outbox_capacity(capacity);
    }
    if let Some(mode) = options.dispatch_mode {
        builder = builder.with_dispatch_mode(parse_dispatch_mode(&mode)?);
    }

    Ok(builder.build())
}

pub fn status_json(entries: Vec<(String, ComponentStatus)>) -> FfiResult<String> {
    let value: Vec<Value> = entries
        .into_iter()
        .map(|(id, status)| serde_json::json!({ "id": id, "status": format!("{status:?}") }))
        .collect();
    serde_json::to_string(&value).map_err(FfiError::engine)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn nested_object_properties_are_not_stringified() {
        let change = json_to_source_change(
            "orders",
            &json!({
                "op": "insert",
                "id": "o1",
                "labels": ["Order"],
                "properties": { "id": "o1", "address": { "city": "Cambridge" } }
            }),
        )
        .unwrap();
        let SourceChange::Insert {
            element: Element::Node { properties, .. },
        } = change
        else {
            panic!("expected node insert");
        };
        let json: serde_json::Value = {
            let map: serde_json::Map<String, serde_json::Value> = (&properties).into();
            map.into()
        };
        assert_eq!(json["address"]["city"], json!("Cambridge"));
    }

    #[test]
    fn insert_node_change() {
        let change = json_to_source_change(
            "orders",
            &json!({
                "op": "insert",
                "id": "o1",
                "labels": ["Order"],
                "properties": { "id": "o1", "total": 42 }
            }),
        )
        .unwrap();
        assert!(matches!(change, SourceChange::Insert { .. }));
    }

    #[test]
    fn unknown_op_is_typed() {
        let err = json_to_source_change("orders", &json!({"op": "merge", "id": "o1"})).unwrap_err();
        assert_eq!(err.code, ErrorCode::UnknownChangeOp);
    }

    #[test]
    fn relation_requires_both_ends() {
        let err = json_to_source_change(
            "graph",
            &json!({"op": "insert", "id": "r1", "startId": "a"}),
        )
        .unwrap_err();
        assert_eq!(err.code, ErrorCode::RelationRequiresBothEnds);
    }
}
