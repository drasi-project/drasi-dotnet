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
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use drasi_lib::{ComponentStatus, DrasiLib, Query};
use tokio::sync::Mutex;

use crate::components::{CsharpReaction, CsharpSource, ReactionCallback, SharedSource};
use crate::conversions::{json_to_source_change, parse_string_list};

pub struct EngineInner {
    pub id: String,
    pub core: DrasiLib,
    pub sources: Mutex<HashMap<String, Arc<CsharpSource>>>,
    /// Queries registered while the engine was stopped, whose auto-start was
    /// suppressed. `drasi-lib` 0.8.9 starts an auto-start query the moment it
    /// is added, without the `is_running()` guard that `add_source` and
    /// `add_reaction` both apply. See [`EngineInner::register_query`].
    deferred_queries: Mutex<Vec<String>>,
    closed: AtomicBool,
}

impl EngineInner {
    fn ensure_open(&self) -> Result<(), String> {
        if self.closed.load(Ordering::Relaxed) {
            return Err(format!("engine '{}' has been closed", self.id));
        }
        Ok(())
    }

    /// Registers a query, suppressing the premature start in `drasi-lib` 0.8.9.
    ///
    /// Remove once drasi-project/drasi-core#639 ships.
    async fn register_query(
        &self,
        mut config: drasi_lib::config::QueryConfig,
    ) -> Result<(), String> {
        let defer = config.auto_start && !self.core.is_running().await;
        if defer {
            config.auto_start = false;
        }
        let id = config.id.clone();
        self.core
            .add_query(config)
            .await
            .map_err(|err| err.to_string())?;
        if defer {
            self.deferred_queries.lock().await.push(id);
        }
        Ok(())
    }

    async fn start_deferred_queries(&self) -> Result<(), String> {
        let ids = self.deferred_queries.lock().await.clone();
        for id in ids {
            let already_running = matches!(
                self.core.get_query_status(&id).await,
                Ok(ComponentStatus::Running)
            );
            if !already_running {
                self.core
                    .start_query(&id)
                    .await
                    .map_err(|err| err.to_string())?;
                self.await_query_running(&id, 30.0).await?;
            }
        }
        Ok(())
    }

    async fn await_query_running(&self, id: &str, timeout_seconds: f64) -> Result<(), String> {
        let timeout = Duration::from_secs_f64(timeout_seconds.max(0.0));
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            match self.core.get_query_status(id).await {
                Ok(ComponentStatus::Running) => return Ok(()),
                Ok(ComponentStatus::Error) => {
                    return Err(format!(
                        "query '{id}' entered the error state while starting"
                    ));
                }
                Err(err) => return Err(err.to_string()),
                _ => {}
            }
            if tokio::time::Instant::now() >= deadline {
                return Err(format!(
                    "query '{id}' did not start within {timeout_seconds}s"
                ));
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    }
}

pub struct EngineHandle {
    pub inner: Arc<EngineInner>,
}

pub async fn create(id: String) -> Result<EngineHandle, String> {
    let core = DrasiLib::builder()
        .with_id(id.clone())
        .build()
        .await
        .map_err(|err| err.to_string())?;
    Ok(EngineHandle {
        inner: Arc::new(EngineInner {
            id,
            core,
            sources: Mutex::new(HashMap::new()),
            deferred_queries: Mutex::new(Vec::new()),
            closed: AtomicBool::new(false),
        }),
    })
}

pub async fn start(handle: &EngineHandle) -> Result<(), String> {
    handle.inner.ensure_open()?;
    handle
        .inner
        .core
        .start()
        .await
        .map_err(|err| err.to_string())?;
    handle.inner.start_deferred_queries().await
}

pub async fn shutdown(handle: &EngineHandle) -> Result<(), String> {
    handle.inner.closed.store(true, Ordering::Relaxed);
    handle
        .inner
        .core
        .shutdown()
        .await
        .map_err(|err| err.to_string())
}

pub async fn add_source(handle: &EngineHandle, id: String) -> Result<(), String> {
    handle.inner.ensure_open()?;
    let source = Arc::new(CsharpSource::new(&id).map_err(|err| err.to_string())?);
    handle
        .inner
        .core
        .add_source(SharedSource(Arc::clone(&source)))
        .await
        .map_err(|err| err.to_string())?;
    handle.inner.sources.lock().await.insert(id, source);
    Ok(())
}

pub async fn add_query(
    handle: &EngineHandle,
    id: String,
    query: String,
    sources_json: &str,
) -> Result<(), String> {
    handle.inner.ensure_open()?;
    let sources = parse_string_list(sources_json, "sources")?;
    let mut builder = Query::cypher(&id).query(query);
    for source in sources {
        builder = builder.from_source(source);
    }
    handle.inner.register_query(builder.build()).await
}

pub async fn add_reaction(
    handle: &EngineHandle,
    id: String,
    query_ids_json: &str,
    callback: ReactionCallback,
    user_data: *mut c_void,
) -> Result<(), String> {
    handle.inner.ensure_open()?;
    let query_ids = parse_string_list(query_ids_json, "query_ids")?;
    handle
        .inner
        .core
        .add_reaction(CsharpReaction::new(&id, query_ids, callback, user_data))
        .await
        .map_err(|err| err.to_string())
}

pub async fn push_change(
    handle: &EngineHandle,
    source_id: String,
    change_json: &str,
) -> Result<(), String> {
    handle.inner.ensure_open()?;
    let value: serde_json::Value = serde_json::from_str(change_json)
        .map_err(|err| format!("change is not valid JSON: {err}"))?;
    let change = json_to_source_change(&source_id, &value)?;
    let source = handle
        .inner
        .sources
        .lock()
        .await
        .get(&source_id)
        .cloned()
        .ok_or_else(|| format!("'{source_id}' is not a C#-defined source"))?;
    source.push(change).await.map_err(|err| err.to_string())
}

pub async fn get_query_results(handle: &EngineHandle, id: String) -> Result<String, String> {
    let rows = handle
        .inner
        .core
        .get_query_results(&id)
        .await
        .map_err(|err| err.to_string())?;
    serde_json::to_string(&rows).map_err(|err| err.to_string())
}

pub async fn wait_for_query(
    handle: &EngineHandle,
    id: String,
    timeout_seconds: f64,
) -> Result<(), String> {
    handle.inner.await_query_running(&id, timeout_seconds).await
}
