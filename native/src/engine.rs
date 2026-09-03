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

//! Opaque engine handle behind the C ABI.

use std::collections::HashMap;
use std::os::raw::c_void;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use drasi_lib::{ComponentStatus, DrasiLib};
use serde_json::Value;
use tokio::sync::Mutex;

use crate::components::{
    BoxedReaction, BoxedSource, CsharpReaction, CsharpSource, ReactionCallback, SharedSource,
};
use crate::conversions::{
    build_query, json_to_source_change, parse_query_options, parse_source_subscriptions,
    parse_string_list, status_json,
};
use crate::error::{ErrorCode, FfiError, FfiResult};
use crate::plugins::PluginHost;
use crate::stores::CreateOptions;
use crate::streams::{pump_broadcast, pump_stream, StreamHandle};

pub struct EngineInner {
    pub id: String,
    pub core: DrasiLib,
    pub sources: Mutex<HashMap<String, Arc<CsharpSource>>>,
    pub plugins: Arc<PluginHost>,
    default_plugin_dir: Mutex<Option<PathBuf>>,
    durable_capable: bool,
    /// Kept alive so identity-plugin function pointers stay valid.
    #[allow(dead_code)]
    identity_host: Option<Arc<PluginHost>>,
    /// Queries registered while the engine was stopped, whose auto-start was
    /// suppressed. `drasi-lib` 0.8.9 starts an auto-start query the moment it
    /// is added, without the `is_running()` guard that `add_source` and
    /// `add_reaction` both apply. See [`EngineInner::register_query`].
    deferred_queries: Mutex<Vec<String>>,
    closed: AtomicBool,
}

impl EngineInner {
    fn ensure_open(&self) -> FfiResult<()> {
        if self.closed.load(Ordering::Relaxed) {
            return Err(FfiError::closed(&self.id));
        }
        Ok(())
    }

    /// Registers a query, suppressing the premature start in `drasi-lib` 0.8.9.
    ///
    /// Remove once drasi-project/drasi-core#639 ships.
    async fn register_query(&self, mut config: drasi_lib::config::QueryConfig) -> FfiResult<()> {
        let defer = config.auto_start && !self.core.is_running().await;
        if defer {
            config.auto_start = false;
        }
        let id = config.id.clone();
        self.core
            .add_query(config)
            .await
            .map_err(FfiError::engine)?;
        self.note_deferred(&id, defer).await;
        Ok(())
    }

    async fn reconfigure_query(
        &self,
        id: &str,
        mut config: drasi_lib::config::QueryConfig,
    ) -> FfiResult<()> {
        let defer = config.auto_start && !self.core.is_running().await;
        if defer {
            config.auto_start = false;
        }
        self.core
            .update_query(id, config)
            .await
            .map_err(FfiError::engine)?;
        self.note_deferred(id, defer).await;
        Ok(())
    }

    async fn note_deferred(&self, id: &str, defer: bool) {
        let mut deferred = self.deferred_queries.lock().await;
        match (defer, deferred.iter().any(|held| held == id)) {
            (true, false) => deferred.push(id.to_string()),
            (false, true) => deferred.retain(|held| held != id),
            _ => {}
        }
    }

    async fn start_deferred_queries(&self) -> FfiResult<()> {
        let ids = self.deferred_queries.lock().await.clone();
        for id in ids {
            let already_running = matches!(
                self.core.get_query_status(&id).await,
                Ok(ComponentStatus::Running)
            );
            if !already_running {
                self.core.start_query(&id).await.map_err(FfiError::engine)?;
                self.await_query_running(&id, 30.0).await?;
            }
        }
        Ok(())
    }

    async fn await_query_running(&self, id: &str, timeout_seconds: f64) -> FfiResult<()> {
        let timeout = Duration::from_secs_f64(timeout_seconds.max(0.0));
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            let last = match self.core.get_query_status(id).await {
                Ok(ComponentStatus::Running) => return Ok(()),
                Ok(ComponentStatus::Error) => {
                    return Err(FfiError::engine(format!(
                        "query '{id}' entered the error state while starting"
                    )));
                }
                Ok(status) => format!("{status:?}"),
                Err(err) => err.to_string(),
            };
            if tokio::time::Instant::now() >= deadline {
                return Err(FfiError::engine(format!(
                    "query '{id}' was still {last} after {timeout_seconds}s"
                )));
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    }

    async fn default_plugin_dir(&self) -> std::io::Result<PathBuf> {
        let mut guard = self.default_plugin_dir.lock().await;
        if let Some(existing) = guard.as_ref() {
            return Ok(existing.clone());
        }
        let directory = user_plugin_root().join(sanitize(&self.id));
        std::fs::create_dir_all(&directory)?;
        *guard = Some(directory.clone());
        Ok(directory)
    }
}

fn user_plugin_root() -> PathBuf {
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        return PathBuf::from(local).join("Drasi").join("plugins");
    }
    if cfg!(target_os = "macos") {
        if let Ok(home) = std::env::var("HOME") {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join("Drasi")
                .join("plugins");
        }
    }
    if let Ok(xdg) = std::env::var("XDG_DATA_HOME") {
        return PathBuf::from(xdg).join("drasi").join("plugins");
    }
    if let Ok(home) = std::env::var("HOME") {
        return PathBuf::from(home)
            .join(".local")
            .join("share")
            .join("drasi")
            .join("plugins");
    }
    PathBuf::from("drasi-plugins")
}

fn sanitize(id: &str) -> String {
    id.chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' || c == '_' {
                c
            } else {
                '_'
            }
        })
        .collect()
}

pub struct EngineHandle {
    pub inner: Arc<EngineInner>,
}

pub async fn create(id: String, options_json: Option<&str>) -> FfiResult<EngineHandle> {
    let options = CreateOptions::parse(options_json)?;
    let plugins_dir = options.plugins_dir.clone();
    let durable_capable = options.has_state_store();
    let wanted_identity = options.identity_plugin()?;
    let identity_secrets = options.secrets.clone();

    let (identity_host, identity_provider) = match wanted_identity {
        Some((kind, config)) => {
            let dir = plugins_dir.clone().ok_or_else(|| {
                FfiError::new(
                    ErrorCode::UnknownIdentityKind,
                    format!(
                        "unknown identity kind '{kind}'; the built-in kinds are \
                         'password' and 'token', and a kind provided by an \
                         identity plugin needs pluginsDir on create"
                    ),
                )
            })?;
            let host = Arc::new(PluginHost::new(identity_secrets));
            host.load_dir_headless(Path::new(&dir))
                .await
                .map_err(FfiError::plugin)?;
            let descriptor = host.identity_descriptor(&kind).await.ok_or_else(|| {
                FfiError::unknown_kind(ErrorCode::UnknownIdentityKind, "identity", &kind)
            })?;
            let provider = descriptor
                .create_identity_provider(&config)
                .await
                .map_err(FfiError::engine)?;
            (Some(host), Some(Arc::from(provider)))
        }
        None => (None, None),
    };
    let (builder, secrets) = options
        .apply(DrasiLib::builder().with_id(id.clone()), identity_provider)
        .await?;
    let core = builder.build().await.map_err(FfiError::engine)?;

    let handle = EngineHandle {
        inner: Arc::new(EngineInner {
            id,
            core,
            sources: Mutex::new(HashMap::new()),
            plugins: Arc::new(PluginHost::new(secrets)),
            default_plugin_dir: Mutex::new(None),
            durable_capable,
            identity_host,
            deferred_queries: Mutex::new(Vec::new()),
            closed: AtomicBool::new(false),
        }),
    };

    if let Some(dir) = plugins_dir {
        handle
            .inner
            .plugins
            .load_dir(&handle.inner.core, &handle.inner.id, Path::new(&dir), None)
            .await
            .map_err(FfiError::plugin)?;
    }

    Ok(handle)
}

pub async fn from_config(json: &str) -> FfiResult<EngineHandle> {
    let value: Value = serde_json::from_str(json)
        .map_err(|err| FfiError::config(format!("config is not valid JSON: {err}")))?;
    let obj = value
        .as_object()
        .ok_or_else(|| FfiError::config("config must be a JSON object"))?;

    let id = obj
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or("drasi")
        .to_string();

    let mut create_opts = value.clone();
    if let Some(map) = create_opts.as_object_mut() {
        map.remove("sources");
        map.remove("queries");
        map.remove("reactions");
    }
    let create_json = serde_json::to_string(&create_opts).map_err(FfiError::engine)?;
    let handle = create(id, Some(&create_json)).await?;
    // Match Python/Node: start after plugins are loaded, then add components
    // so auto-start sources/queries/reactions actually run.
    start(&handle).await?;

    if let Some(sources) = obj.get("sources").and_then(Value::as_array) {
        for source in sources {
            let source_obj = source
                .as_object()
                .ok_or_else(|| FfiError::config("each entry in 'sources' must be an object"))?;
            let kind = source_obj
                .get("kind")
                .and_then(Value::as_str)
                .unwrap_or("csharp");
            let source_id = source_obj
                .get("id")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::config("a source is missing 'id'"))?
                .to_string();
            let auto_start = source_obj
                .get("autoStart")
                .or_else(|| source_obj.get("auto_start"))
                .and_then(Value::as_bool)
                .unwrap_or(true);
            if kind == "csharp" {
                add_source(&handle, source_id, auto_start).await?;
            } else {
                let mut config = source.clone();
                if let Some(map) = config.as_object_mut() {
                    map.remove("kind");
                    map.remove("id");
                    map.remove("autoStart");
                    map.remove("auto_start");
                    map.remove("bootstrap");
                }
                let config_json = serde_json::to_string(&config).map_err(FfiError::engine)?;
                let bootstrap = source_obj
                    .get("bootstrap")
                    .map(serde_json::to_string)
                    .transpose()
                    .map_err(FfiError::engine)?;
                add_plugin_source(
                    &handle,
                    kind.to_string(),
                    source_id,
                    &config_json,
                    auto_start,
                    bootstrap.as_deref(),
                )
                .await?;
            }
        }
    }

    if let Some(queries) = obj.get("queries").and_then(Value::as_array) {
        for query in queries {
            let query_obj = query
                .as_object()
                .ok_or_else(|| FfiError::config("each entry in 'queries' must be an object"))?;
            let query_id = query_obj
                .get("id")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::config("a query is missing 'id'"))?
                .to_string();
            let text = query_obj
                .get("query")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::config(format!("query '{query_id}' is missing 'query'")))?
                .to_string();
            let sources = query_obj
                .get("sources")
                .cloned()
                .unwrap_or(Value::Array(vec![]));
            let sources_json = serde_json::to_string(&sources).map_err(FfiError::engine)?;
            let mut options = query.clone();
            if let Some(map) = options.as_object_mut() {
                map.remove("id");
                map.remove("query");
                map.remove("sources");
            }
            let options_json = serde_json::to_string(&options).map_err(FfiError::engine)?;
            add_query(&handle, query_id, text, &sources_json, Some(&options_json)).await?;
        }
    }

    if let Some(reactions) = obj.get("reactions").and_then(Value::as_array) {
        for reaction in reactions {
            let reaction_obj = reaction
                .as_object()
                .ok_or_else(|| FfiError::config("each entry in 'reactions' must be an object"))?;
            let kind = reaction_obj
                .get("kind")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::config("a reaction is missing 'kind'"))?;
            let reaction_id = reaction_obj
                .get("id")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::config("a reaction is missing 'id'"))?
                .to_string();
            let query_ids = reaction_obj
                .get("queryIds")
                .or_else(|| reaction_obj.get("query_ids"))
                .cloned()
                .unwrap_or(Value::Array(vec![]));
            let query_ids_json = serde_json::to_string(&query_ids).map_err(FfiError::engine)?;
            let auto_start = reaction_obj
                .get("autoStart")
                .or_else(|| reaction_obj.get("auto_start"))
                .and_then(Value::as_bool)
                .unwrap_or(true);
            let mut config = reaction.clone();
            if let Some(map) = config.as_object_mut() {
                map.remove("kind");
                map.remove("id");
                map.remove("queryIds");
                map.remove("query_ids");
                map.remove("autoStart");
                map.remove("auto_start");
            }
            let config_json = serde_json::to_string(&config).map_err(FfiError::engine)?;
            add_plugin_reaction(
                &handle,
                kind.to_string(),
                reaction_id,
                &query_ids_json,
                &config_json,
                auto_start,
            )
            .await?;
        }
    }

    Ok(handle)
}

pub async fn start(handle: &EngineHandle) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle.inner.core.start().await.map_err(FfiError::engine)?;
    handle.inner.start_deferred_queries().await
}

pub async fn stop(handle: &EngineHandle) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle.inner.core.stop().await.map_err(FfiError::engine)
}

pub async fn shutdown(handle: &EngineHandle) -> FfiResult<()> {
    if handle.inner.closed.swap(true, Ordering::Relaxed) {
        return Ok(());
    }
    handle.inner.core.shutdown().await.map_err(FfiError::engine)
}

pub async fn is_running(handle: &EngineHandle) -> bool {
    handle.inner.core.is_running().await
}

pub async fn add_source(handle: &EngineHandle, id: String, auto_start: bool) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let source = Arc::new(CsharpSource::new(&id, auto_start).map_err(FfiError::engine)?);
    handle
        .inner
        .core
        .add_source(SharedSource(Arc::clone(&source)))
        .await
        .map_err(FfiError::engine)?;
    handle.inner.sources.lock().await.insert(id, source);
    Ok(())
}

pub async fn remove_source(handle: &EngineHandle, id: String, cleanup: bool) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .remove_source(&id, cleanup)
        .await
        .map_err(FfiError::engine)?;
    handle.inner.sources.lock().await.remove(&id);
    Ok(())
}

pub async fn start_source(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .start_source(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn stop_source(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .stop_source(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn source_status(handle: &EngineHandle, id: String) -> FfiResult<String> {
    handle
        .inner
        .core
        .get_source_status(&id)
        .await
        .map(|status| format!("{status:?}"))
        .map_err(FfiError::engine)
}

pub async fn list_sources(handle: &EngineHandle) -> FfiResult<String> {
    let sources = handle
        .inner
        .core
        .list_sources()
        .await
        .map_err(FfiError::engine)?;
    status_json(sources)
}

pub async fn add_query(
    handle: &EngineHandle,
    id: String,
    query: String,
    sources_json: &str,
    options_json: Option<&str>,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let sources = parse_source_subscriptions(sources_json)?;
    let options = parse_query_options(options_json)?;
    let config = build_query(&id, &query, &sources, options)?;
    handle.inner.register_query(config).await
}

pub async fn update_query(
    handle: &EngineHandle,
    id: String,
    query: String,
    sources_json: &str,
    options_json: Option<&str>,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let sources = parse_source_subscriptions(sources_json)?;
    let options = parse_query_options(options_json)?;
    let config = build_query(&id, &query, &sources, options)?;
    handle.inner.reconfigure_query(&id, config).await
}

pub async fn remove_query(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .remove_query(&id)
        .await
        .map_err(FfiError::engine)?;
    handle.inner.note_deferred(&id, false).await;
    Ok(())
}

pub async fn start_query(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .start_query(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn stop_query(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .stop_query(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn add_reaction(
    handle: &EngineHandle,
    id: String,
    query_ids_json: &str,
    callback: ReactionCallback,
    user_data: *mut c_void,
    auto_start: bool,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let query_ids = parse_string_list(query_ids_json, "query_ids")?;
    handle
        .inner
        .core
        .add_reaction(CsharpReaction::new(
            &id, query_ids, callback, user_data, auto_start,
        ))
        .await
        .map_err(FfiError::engine)
}

pub async fn add_durable_reaction(
    handle: &EngineHandle,
    id: String,
    query_ids_json: &str,
    callback: ReactionCallback,
    user_data: *mut c_void,
    recovery_policy: &str,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    if !handle.inner.durable_capable {
        return Err(FfiError::new(
            ErrorCode::DurableRequiresStateStore,
            "a durable reaction needs somewhere to keep its checkpoint; \
             pass stateStore={kind:'redb', path:...} to Create",
        ));
    }
    let query_ids = parse_string_list(query_ids_json, "query_ids")?;
    let recovery = parse_recovery_policy(recovery_policy)?;
    handle
        .inner
        .core
        .add_reaction(CsharpReaction::durable(
            &id, query_ids, callback, user_data, recovery,
        ))
        .await
        .map_err(FfiError::engine)
}

pub async fn remove_reaction(handle: &EngineHandle, id: String, cleanup: bool) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .remove_reaction(&id, cleanup)
        .await
        .map_err(FfiError::engine)
}

pub async fn start_reaction(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .start_reaction(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn stop_reaction(handle: &EngineHandle, id: String) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .stop_reaction(&id)
        .await
        .map_err(FfiError::engine)
}

pub async fn reaction_status(handle: &EngineHandle, id: String) -> FfiResult<String> {
    handle
        .inner
        .core
        .get_reaction_status(&id)
        .await
        .map(|status| format!("{status:?}"))
        .map_err(FfiError::engine)
}

pub async fn list_reactions(handle: &EngineHandle) -> FfiResult<String> {
    let reactions = handle
        .inner
        .core
        .list_reactions()
        .await
        .map_err(FfiError::engine)?;
    status_json(reactions)
}

pub async fn push_change(
    handle: &EngineHandle,
    source_id: String,
    change_json: &str,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let value: Value = serde_json::from_str(change_json)
        .map_err(|err| FfiError::config(format!("change is not valid JSON: {err}")))?;
    let change = json_to_source_change(&source_id, &value)?;
    let source = handle
        .inner
        .sources
        .lock()
        .await
        .get(&source_id)
        .cloned()
        .ok_or_else(|| {
            FfiError::new(
                ErrorCode::NoCsharpSource,
                format!("'{source_id}' is not a C#-defined source"),
            )
        })?;
    source.push(change).await.map_err(FfiError::engine)
}

pub async fn get_query_results(handle: &EngineHandle, id: String) -> FfiResult<String> {
    let rows = handle
        .inner
        .core
        .get_query_results(&id)
        .await
        .map_err(FfiError::engine)?;
    serde_json::to_string(&rows).map_err(FfiError::engine)
}

pub async fn query_status(handle: &EngineHandle, id: String) -> FfiResult<String> {
    handle
        .inner
        .core
        .get_query_status(&id)
        .await
        .map(|status| format!("{status:?}"))
        .map_err(FfiError::engine)
}

pub async fn list_queries(handle: &EngineHandle) -> FfiResult<String> {
    let queries = handle
        .inner
        .core
        .list_queries()
        .await
        .map_err(FfiError::engine)?;
    status_json(queries)
}

pub async fn wait_for_query(
    handle: &EngineHandle,
    id: String,
    timeout_seconds: f64,
) -> FfiResult<()> {
    handle.inner.await_query_running(&id, timeout_seconds).await
}

pub async fn query_metrics(handle: &EngineHandle, id: String) -> FfiResult<String> {
    let m = handle
        .inner
        .core
        .get_query_output_metrics(&id)
        .await
        .map_err(FfiError::engine)?;
    serde_json::to_string(&serde_json::json!({
        "outboxSize": m.outbox_size,
        "outboxEarliestSeq": m.outbox_earliest_seq,
        "outboxLatestSeq": m.outbox_latest_seq,
        "resultSeqAdvances": m.result_seq_advances,
        "liveResultsCount": m.live_results_count,
        "outerTransactionDurationNsLast": m.outer_transaction_duration_ns_last,
        "outerTransactionDurationNsMax": m.outer_transaction_duration_ns_max,
        "snapshotFetchCount": m.snapshot_fetch_count,
    }))
    .map_err(FfiError::engine)
}

pub async fn reaction_metrics(handle: &EngineHandle, id: String) -> FfiResult<String> {
    let metrics = handle
        .inner
        .core
        .get_reaction_metrics(&id)
        .await
        .map_err(FfiError::engine)?;
    let mut map = serde_json::Map::new();
    for (query_id, m) in metrics {
        map.insert(
            query_id,
            serde_json::json!({
                "checkpointSequence": m.checkpoint_sequence,
                "checkpointLag": m.checkpoint_lag,
                "dedupSkipCount": m.dedup_skip_count,
                "gapDetectionCount": m.gap_detection_count,
                "recoveryStrictCount": m.recovery_strict_count,
                "recoveryAutoResetCount": m.recovery_auto_reset_count,
                "recoveryAutoSkipGapCount": m.recovery_auto_skip_gap_count,
                "fetchSnapshotCount": m.fetch_snapshot_count,
                "fetchOutboxCount": m.fetch_outbox_count,
            }),
        );
    }
    serde_json::to_string(&Value::Object(map)).map_err(FfiError::engine)
}

pub async fn lifecycle_metrics(handle: &EngineHandle) -> FfiResult<String> {
    let m = handle
        .inner
        .core
        .get_lifecycle_metrics()
        .await
        .map_err(FfiError::engine)?;
    serde_json::to_string(&serde_json::json!({
        "startupRejectionDurableNoStore": m.startup_rejection_durable_no_store,
        "startupRejectionDurableOnVolatile": m.startup_rejection_durable_on_volatile,
        "startupRejectionSnapshotSkipGap": m.startup_rejection_snapshot_skip_gap,
        "startupRejectionNoSnapshotAutoReset": m.startup_rejection_no_snapshot_auto_reset,
        "autoResetCompletions": m.auto_reset_completions,
        "hashMismatchCount": m.hash_mismatch_count,
    }))
    .map_err(FfiError::engine)
}

pub async fn source_schema(handle: &EngineHandle, id: String) -> FfiResult<String> {
    let schema = handle
        .inner
        .core
        .get_source_schema(&id)
        .await
        .map_err(FfiError::engine)?;
    match schema {
        Some(schema) => serde_json::to_string(&schema).map_err(FfiError::engine),
        None => Ok("null".into()),
    }
}

pub async fn graph_schema(handle: &EngineHandle) -> FfiResult<String> {
    let schema = handle
        .inner
        .core
        .get_graph_schema()
        .await
        .map_err(FfiError::engine)?;
    serde_json::to_string(&schema).map_err(FfiError::engine)
}

pub async fn subscribe_query_events(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_query_events(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

pub async fn subscribe_source_events(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_source_events(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

pub async fn subscribe_reaction_events(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_reaction_events(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

pub async fn subscribe_all_events(
    handle: &EngineHandle,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let events = handle
        .inner
        .core
        .get_all_events()
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_stream(events, callback, user_data as usize))
}

pub async fn subscribe_query_logs(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_query_logs(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

pub async fn subscribe_source_logs(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_source_logs(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

pub async fn subscribe_reaction_logs(
    handle: &EngineHandle,
    id: String,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> FfiResult<StreamHandle> {
    let (history, receiver) = handle
        .inner
        .core
        .subscribe_reaction_logs(&id)
        .await
        .map_err(FfiError::engine)?;
    Ok(pump_broadcast(
        history,
        receiver,
        callback,
        user_data as usize,
    ))
}

fn parse_recovery_policy(policy: &str) -> FfiResult<drasi_lib::ReactionRecoveryPolicy> {
    match policy
        .trim()
        .to_ascii_lowercase()
        .replace('-', "_")
        .as_str()
    {
        "strict" => Ok(drasi_lib::ReactionRecoveryPolicy::Strict),
        "auto_reset" => Ok(drasi_lib::ReactionRecoveryPolicy::AutoReset),
        "skip_gap" | "auto_skip_gap" => Ok(drasi_lib::ReactionRecoveryPolicy::AutoSkipGap),
        other => Err(FfiError::config(format!(
            "unknown recovery policy '{other}', expected 'strict', 'auto_reset' or 'skip_gap'"
        ))),
    }
}

pub async fn add_plugin_source(
    handle: &EngineHandle,
    kind: String,
    id: String,
    config_json: &str,
    auto_start: bool,
    bootstrap_json: Option<&str>,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let config: Value = serde_json::from_str(config_json)
        .map_err(|err| FfiError::config(format!("source config is not valid JSON: {err}")))?;
    let descriptor = handle
        .inner
        .plugins
        .source_descriptor(&kind)
        .await
        .ok_or_else(|| FfiError::unknown_kind(ErrorCode::UnknownSourceKind, "source", &kind))?;
    let source = descriptor
        .create_source(&id, &config, auto_start)
        .await
        .map_err(FfiError::engine)?;
    if let Some(bootstrap_json) = bootstrap_json {
        let bootstrap: Value = serde_json::from_str(bootstrap_json)
            .map_err(|err| FfiError::config(format!("bootstrap is not valid JSON: {err}")))?;
        let bootstrap_kind = bootstrap
            .get("kind")
            .and_then(Value::as_str)
            .ok_or_else(|| {
                FfiError::new(
                    ErrorCode::BootstrapKindRequired,
                    "bootstrap requires a 'kind'",
                )
            })?;
        let mut bootstrap_config = bootstrap.clone();
        if let Some(map) = bootstrap_config.as_object_mut() {
            map.remove("kind");
        }
        let bootstrapper = handle
            .inner
            .plugins
            .bootstrap_descriptor(bootstrap_kind)
            .await
            .ok_or_else(|| {
                FfiError::unknown_kind(ErrorCode::UnknownBootstrapKind, "bootstrap", bootstrap_kind)
            })?;
        let provider = bootstrapper
            .create_bootstrap_provider(&bootstrap_config, &config)
            .await
            .map_err(FfiError::engine)?;
        source.set_bootstrap_provider(provider).await;
    }
    handle
        .inner
        .core
        .add_source_with_metadata(
            BoxedSource(source),
            HashMap::from([("pluginKind".to_string(), kind)]),
        )
        .await
        .map_err(FfiError::engine)
}

pub async fn add_plugin_reaction(
    handle: &EngineHandle,
    kind: String,
    id: String,
    query_ids_json: &str,
    config_json: &str,
    auto_start: bool,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let config: Value = serde_json::from_str(config_json)
        .map_err(|err| FfiError::config(format!("reaction config is not valid JSON: {err}")))?;
    let query_ids = parse_string_list(query_ids_json, "query_ids")?;
    let descriptor = handle
        .inner
        .plugins
        .reaction_descriptor(&kind)
        .await
        .ok_or_else(|| FfiError::unknown_kind(ErrorCode::UnknownReactionKind, "reaction", &kind))?;
    let reaction = descriptor
        .create_reaction(&id, query_ids, &config, auto_start)
        .await
        .map_err(FfiError::engine)?;
    handle
        .inner
        .core
        .add_reaction_with_metadata(
            BoxedReaction(reaction),
            HashMap::from([("pluginKind".to_string(), kind)]),
        )
        .await
        .map_err(FfiError::engine)
}

pub async fn update_plugin_source(
    handle: &EngineHandle,
    kind: String,
    id: String,
    config_json: &str,
    auto_start: bool,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let config: Value = serde_json::from_str(config_json)
        .map_err(|err| FfiError::config(format!("source config is not valid JSON: {err}")))?;
    let descriptor = handle
        .inner
        .plugins
        .source_descriptor(&kind)
        .await
        .ok_or_else(|| FfiError::unknown_kind(ErrorCode::UnknownSourceKind, "source", &kind))?;
    let source = descriptor
        .create_source(&id, &config, auto_start)
        .await
        .map_err(FfiError::engine)?;
    handle
        .inner
        .core
        .update_source(&id, BoxedSource(source))
        .await
        .map_err(FfiError::engine)
}

pub async fn update_plugin_reaction(
    handle: &EngineHandle,
    kind: String,
    id: String,
    query_ids_json: &str,
    config_json: &str,
    auto_start: bool,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let config: Value = serde_json::from_str(config_json)
        .map_err(|err| FfiError::config(format!("reaction config is not valid JSON: {err}")))?;
    let query_ids = parse_string_list(query_ids_json, "query_ids")?;
    let descriptor = handle
        .inner
        .plugins
        .reaction_descriptor(&kind)
        .await
        .ok_or_else(|| FfiError::unknown_kind(ErrorCode::UnknownReactionKind, "reaction", &kind))?;
    let reaction = descriptor
        .create_reaction(&id, query_ids, &config, auto_start)
        .await
        .map_err(FfiError::engine)?;
    handle
        .inner
        .core
        .update_reaction(&id, BoxedReaction(reaction))
        .await
        .map_err(FfiError::engine)
}

pub async fn load_plugins(
    handle: &EngineHandle,
    directory: String,
    verify_json: Option<&str>,
) -> FfiResult<String> {
    handle.inner.ensure_open()?;
    let verify: Option<HashMap<String, String>> = match verify_json {
        None | Some("") => None,
        Some(raw) => Some(
            serde_json::from_str(raw)
                .map_err(|err| FfiError::config(format!("verify map is not valid JSON: {err}")))?,
        ),
    };
    let summary = handle
        .inner
        .plugins
        .load_dir(
            &handle.inner.core,
            &handle.inner.id,
            Path::new(&directory),
            verify.as_ref(),
        )
        .await
        .map_err(FfiError::plugin)?;
    serde_json::to_string(&serde_json::json!({
        "plugins": summary.plugins,
        "sources": summary.sources,
        "reactions": summary.reactions,
        "bootstrap": summary.bootstrap,
        "secretStores": summary.secret_stores,
        "identityProviders": summary.identity_providers,
        "skipped": summary.skipped,
    }))
    .map_err(FfiError::engine)
}

pub async fn watch_plugins(
    handle: &EngineHandle,
    directory: String,
    debounce_seconds: f64,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .plugins
        .watch(
            handle.inner.core.clone(),
            handle.inner.id.clone(),
            PathBuf::from(directory),
            Duration::from_secs_f64(debounce_seconds.max(0.0)),
        )
        .await
        .map_err(FfiError::plugin)
}

pub async fn plugin_kinds(handle: &EngineHandle) -> FfiResult<String> {
    let sources = handle.inner.plugins.source_kinds().await;
    let reactions = handle.inner.plugins.reaction_kinds().await;
    let bootstrap = handle.inner.plugins.bootstrap_kinds().await;
    let secret_stores = handle.inner.plugins.secret_store_kinds().await;
    let identity_providers = handle.inner.plugins.identity_kinds().await;
    serde_json::to_string(&serde_json::json!({
        "sources": sources,
        "reactions": reactions,
        "bootstrap": bootstrap,
        "secretStores": secret_stores,
        "identityProviders": identity_providers,
    }))
    .map_err(FfiError::engine)
}

pub fn host_info() -> String {
    crate::plugins::describe_host().to_string()
}

pub async fn search_plugins(query: Option<&str>) -> FfiResult<String> {
    let client = crate::plugins::registry_client(Vec::new(), false);
    let found = client
        .search_plugins(query.unwrap_or(""))
        .await
        .map_err(FfiError::plugin)?;
    let mapped: Vec<Value> = found
        .into_iter()
        .map(|entry| {
            let (plugin_type, kind) = entry
                .reference
                .split_once('/')
                .unwrap_or(("", entry.reference.as_str()));
            serde_json::json!({
                "reference": entry.reference,
                "fullReference": entry.full_reference,
                "pluginType": plugin_type,
                "kind": kind,
                "versions": entry.versions.iter().map(|v| serde_json::json!({
                    "version": v.version,
                    "platforms": v.platforms,
                })).collect::<Vec<_>>(),
            })
        })
        .collect();
    serde_json::to_string(&mapped).map_err(FfiError::engine)
}

pub async fn list_plugin_tags(repository: String) -> FfiResult<String> {
    let client = crate::plugins::registry_client(Vec::new(), false);
    let tags = client
        .list_tags(&repository)
        .await
        .map_err(FfiError::plugin)?;
    serde_json::to_string(&tags).map_err(FfiError::engine)
}

pub async fn resolve_plugin(reference: String) -> FfiResult<String> {
    let client = crate::plugins::registry_client(Vec::new(), false);
    let resolved = crate::plugins::resolve(&client, &reference)
        .await
        .map_err(FfiError::incompatible)?;
    serde_json::to_string(&serde_json::json!({
        "reference": resolved.reference,
        "kind": resolved.kind,
        "pluginType": resolved.plugin_type,
        "version": resolved.version,
        "targetTriple": resolved.target_triple,
        "sdkVersion": resolved.sdk_version,
        "coreVersion": resolved.core_version,
        "libVersion": resolved.lib_version,
    }))
    .map_err(FfiError::engine)
}

#[derive(serde::Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct InstallOptions {
    directory: Option<String>,
    #[serde(default)]
    verify: bool,
    #[serde(default)]
    require_signed: bool,
    trusted_identities: Option<Vec<(String, String)>>,
    #[serde(default = "default_true")]
    load: bool,
}

fn default_true() -> bool {
    true
}

pub async fn install_plugin(
    handle: &EngineHandle,
    reference: String,
    options_json: Option<&str>,
) -> FfiResult<String> {
    handle.inner.ensure_open()?;
    let options: InstallOptions = match options_json {
        None | Some("") => InstallOptions {
            load: true,
            ..Default::default()
        },
        Some(raw) => serde_json::from_str(raw).map_err(|err| {
            FfiError::config(format!("install options are not valid JSON: {err}"))
        })?,
    };
    let client = crate::plugins::registry_client(
        options.trusted_identities.unwrap_or_default(),
        options.verify || options.require_signed,
    );
    let resolved = crate::plugins::resolve(&client, &reference)
        .await
        .map_err(FfiError::incompatible)?;
    let directory = match options.directory {
        Some(directory) => PathBuf::from(directory),
        None => handle
            .inner
            .default_plugin_dir()
            .await
            .map_err(FfiError::plugin)?,
    };
    tokio::fs::create_dir_all(&directory)
        .await
        .map_err(FfiError::plugin)?;
    let file_name = crate::plugins::plugin_file_name(&resolved.plugin_type, &resolved.kind);
    let download = client
        .download_plugin(&resolved.reference, &directory, &file_name)
        .await
        .map_err(FfiError::plugin)?;
    let status = crate::plugins::signature_status(&download.verification);
    if options.require_signed && status != "verified" {
        return Err(FfiError::new(
            ErrorCode::PluginSignatureInvalid,
            format!(
                "'{}' could not be verified (signature status: {status})",
                resolved.reference
            ),
        ));
    }
    handle
        .inner
        .plugins
        .record_install(&resolved, &download.path)
        .await;
    if options.load {
        handle
            .inner
            .plugins
            .load_file(&handle.inner.core, &handle.inner.id, &download.path)
            .await
            .map_err(FfiError::incompatible)?;
    }
    serde_json::to_string(&serde_json::json!({
        "reference": resolved.reference,
        "kind": resolved.kind,
        "pluginType": resolved.plugin_type,
        "version": resolved.version,
        "path": download.path,
        "verification": status,
        "loaded": options.load,
    }))
    .map_err(FfiError::engine)
}

pub async fn pull_plugin(
    reference: String,
    directory: String,
    filename: String,
    options_json: Option<&str>,
) -> FfiResult<String> {
    let options: InstallOptions = match options_json {
        None | Some("") => InstallOptions::default(),
        Some(raw) => serde_json::from_str(raw)
            .map_err(|err| FfiError::config(format!("pull options are not valid JSON: {err}")))?,
    };
    let client = crate::plugins::registry_client(
        options.trusted_identities.unwrap_or_default(),
        options.verify || options.require_signed,
    );
    let directory = PathBuf::from(directory);
    tokio::fs::create_dir_all(&directory)
        .await
        .map_err(FfiError::plugin)?;
    let download = client
        .download_plugin(&reference, &directory, &filename)
        .await
        .map_err(FfiError::plugin)?;
    let status = crate::plugins::signature_status(&download.verification);
    if options.require_signed && status != "verified" {
        return Err(FfiError::new(
            ErrorCode::PluginSignatureInvalid,
            format!("'{reference}' could not be verified (signature status: {status})"),
        ));
    }
    serde_json::to_string(&serde_json::json!({
        "reference": reference,
        "path": download.path,
        "verification": status,
    }))
    .map_err(FfiError::engine)
}

pub async fn write_lockfile(handle: &EngineHandle, directory: String) -> FfiResult<String> {
    let count = handle
        .inner
        .plugins
        .write_lockfile(Path::new(&directory))
        .await
        .map_err(FfiError::plugin)?;
    Ok(count.to_string())
}

pub fn read_lockfile(directory: String) -> FfiResult<String> {
    let entries = PluginHost::read_lockfile(Path::new(&directory)).map_err(FfiError::plugin)?;
    serde_json::to_string(&entries).map_err(FfiError::engine)
}

pub async fn install_from_lockfile(
    handle: &EngineHandle,
    directory: String,
    load: bool,
) -> FfiResult<String> {
    handle.inner.ensure_open()?;
    let entries = PluginHost::read_lockfile(Path::new(&directory)).map_err(FfiError::plugin)?;
    let client = crate::plugins::registry_client(Vec::new(), false);
    let dir = PathBuf::from(&directory);
    let mut installed = Vec::new();
    for entry in entries {
        let download = client
            .download_plugin(&entry.reference, &dir, &entry.filename)
            .await
            .map_err(FfiError::plugin)?;
        if let Some(expected) = &entry.file_hash {
            let actual = crate::plugins::file_hash(&download.path).map_err(FfiError::plugin)?;
            if !actual.eq_ignore_ascii_case(expected) {
                return Err(FfiError::new(
                    ErrorCode::PluginSignatureInvalid,
                    format!(
                        "'{}' does not match the hash recorded in plugins.lock",
                        entry.reference
                    ),
                ));
            }
        }
        if load {
            handle
                .inner
                .plugins
                .load_file(&handle.inner.core, &handle.inner.id, &download.path)
                .await
                .map_err(FfiError::incompatible)?;
        }
        installed.push(entry.reference);
    }
    serde_json::to_string(&installed).map_err(FfiError::engine)
}

async fn config_schema(
    handle: &EngineHandle,
    kind: &str,
    what: &str,
    code: ErrorCode,
) -> FfiResult<String> {
    let (name, schema) = match what {
        "source" => {
            let d = handle
                .inner
                .plugins
                .source_descriptor(kind)
                .await
                .ok_or_else(|| FfiError::unknown_kind(code, what, kind))?;
            (d.config_schema_name().to_string(), d.config_schema_json())
        }
        "reaction" => {
            let d = handle
                .inner
                .plugins
                .reaction_descriptor(kind)
                .await
                .ok_or_else(|| FfiError::unknown_kind(code, what, kind))?;
            (d.config_schema_name().to_string(), d.config_schema_json())
        }
        "bootstrap" => {
            let d = handle
                .inner
                .plugins
                .bootstrap_descriptor(kind)
                .await
                .ok_or_else(|| FfiError::unknown_kind(code, what, kind))?;
            (d.config_schema_name().to_string(), d.config_schema_json())
        }
        "secretStore" => {
            let d = handle
                .inner
                .plugins
                .secret_store_descriptor(kind)
                .await
                .ok_or_else(|| FfiError::unknown_kind(code, what, kind))?;
            (d.config_schema_name().to_string(), d.config_schema_json())
        }
        _ => return Err(FfiError::config("unknown schema kind")),
    };
    serde_json::to_string(&serde_json::json!({ "name": name, "schema": schema }))
        .map_err(FfiError::engine)
}

pub async fn source_config_schema(handle: &EngineHandle, kind: String) -> FfiResult<String> {
    config_schema(handle, &kind, "source", ErrorCode::UnknownSourceKind).await
}
pub async fn reaction_config_schema(handle: &EngineHandle, kind: String) -> FfiResult<String> {
    config_schema(handle, &kind, "reaction", ErrorCode::UnknownReactionKind).await
}
pub async fn bootstrap_config_schema(handle: &EngineHandle, kind: String) -> FfiResult<String> {
    config_schema(handle, &kind, "bootstrap", ErrorCode::UnknownBootstrapKind).await
}
pub async fn secret_store_config_schema(handle: &EngineHandle, kind: String) -> FfiResult<String> {
    config_schema(
        handle,
        &kind,
        "secretStore",
        ErrorCode::UnknownSecretStoreKind,
    )
    .await
}

pub async fn use_secret_store(
    handle: &EngineHandle,
    kind: String,
    config_json: &str,
) -> FfiResult<()> {
    handle.inner.ensure_open()?;
    let config: Value = serde_json::from_str(config_json)
        .map_err(|err| FfiError::config(format!("secret store config is not valid JSON: {err}")))?;
    let descriptor = handle
        .inner
        .plugins
        .secret_store_descriptor(&kind)
        .await
        .ok_or_else(|| {
            FfiError::unknown_kind(ErrorCode::UnknownSecretStoreKind, "secret store", &kind)
        })?;
    let provider = descriptor
        .create_secret_store(&config)
        .await
        .map_err(FfiError::engine)?;
    handle
        .inner
        .plugins
        .set_secret_store(provider)
        .map_err(FfiError::engine)
}
