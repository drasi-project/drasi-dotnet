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

//! C#-defined source and reaction, mirroring PythonSource / PythonReaction.

use std::any::Any;
use std::collections::HashMap;
use std::ffi::CString;
use std::os::raw::{c_char, c_void};
use std::sync::Arc;

use anyhow::Result;
use async_trait::async_trait;
use drasi_core::models::SourceChange;
use drasi_lib::channels::{QueryResult, SubscriptionResponse};
use drasi_lib::config::SourceSubscriptionSettings;
use drasi_lib::context::{ReactionRuntimeContext, SourceRuntimeContext};
use drasi_lib::{
    ComponentStatus, Reaction, ReactionBase, ReactionBaseParams, Source, SourceBase,
    SourceBaseParams,
};
use serde_json::Value;
use tokio::sync::Mutex as TokioMutex;

const CSHARP_COMPONENT_TYPE: &str = "csharp";

pub type ReactionCallback = unsafe extern "C" fn(*const c_char, *mut c_void);

/// A source that emits the changes pushed into it from managed code.
pub struct CsharpSource {
    base: SourceBase,
    /// Serialises dispatch so concurrent pushes cannot be reordered.
    ///
    /// `dispatch_source_change` assigns a monotonic sequence with `fetch_add`
    /// and only then awaits its way to the subscribers, so two overlapping
    /// pushes can be delivered in the opposite order to the sequences they
    /// took. The query side treats sequence order as delivery order.
    dispatch: TokioMutex<()>,
}

impl CsharpSource {
    pub fn new(id: &str) -> Result<Self> {
        let params = SourceBaseParams::new(id).with_auto_start(true);
        Ok(Self {
            base: SourceBase::new(params)?,
            dispatch: TokioMutex::new(()),
        })
    }

    pub async fn push(&self, change: SourceChange) -> Result<()> {
        let _ordered = self.dispatch.lock().await;
        self.base.dispatch_source_change(change).await
    }
}

#[async_trait]
impl Source for CsharpSource {
    fn id(&self) -> &str {
        self.base.get_id()
    }

    fn type_name(&self) -> &str {
        CSHARP_COMPONENT_TYPE
    }

    fn properties(&self) -> HashMap<String, Value> {
        HashMap::new()
    }

    fn auto_start(&self) -> bool {
        self.base.get_auto_start()
    }

    fn supports_replay(&self) -> bool {
        false
    }

    async fn start(&self) -> Result<()> {
        self.base.set_status(ComponentStatus::Running, None).await;
        Ok(())
    }

    async fn stop(&self) -> Result<()> {
        self.base.stop_common().await
    }

    async fn status(&self) -> ComponentStatus {
        self.base.get_status().await
    }

    async fn subscribe(
        &self,
        settings: SourceSubscriptionSettings,
    ) -> Result<SubscriptionResponse> {
        self.base
            .subscribe_with_bootstrap(&settings, CSHARP_COMPONENT_TYPE)
            .await
    }

    fn as_any(&self) -> &dyn Any {
        self
    }

    async fn initialize(&self, context: SourceRuntimeContext) {
        self.base.initialize(context).await;
    }
}

/// Shared wrapper so the engine can keep an `Arc` after `add_source` takes ownership.
pub struct SharedSource(pub Arc<CsharpSource>);

#[async_trait]
impl Source for SharedSource {
    fn id(&self) -> &str {
        self.0.id()
    }

    fn type_name(&self) -> &str {
        self.0.type_name()
    }

    fn properties(&self) -> HashMap<String, Value> {
        self.0.properties()
    }

    fn auto_start(&self) -> bool {
        self.0.auto_start()
    }

    fn supports_replay(&self) -> bool {
        self.0.supports_replay()
    }

    async fn start(&self) -> Result<()> {
        self.0.start().await
    }

    async fn stop(&self) -> Result<()> {
        self.0.stop().await
    }

    async fn status(&self) -> ComponentStatus {
        self.0.status().await
    }

    async fn subscribe(
        &self,
        settings: SourceSubscriptionSettings,
    ) -> Result<SubscriptionResponse> {
        self.0.subscribe(settings).await
    }

    fn as_any(&self) -> &dyn Any {
        self
    }

    async fn initialize(&self, context: SourceRuntimeContext) {
        self.0.initialize(context).await;
    }
}

/// A reaction that forwards each query result to a managed callback as JSON.
pub struct CsharpReaction {
    base: ReactionBase,
    callback: ReactionCallback,
    user_data: usize,
}

impl CsharpReaction {
    pub fn new(
        id: &str,
        query_ids: Vec<String>,
        callback: ReactionCallback,
        user_data: *mut c_void,
    ) -> Self {
        Self {
            base: ReactionBase::new(ReactionBaseParams::new(id, query_ids)),
            callback,
            user_data: user_data as usize,
        }
    }
}

fn dispatch(callback: ReactionCallback, user_data: usize, result: &QueryResult) -> Result<()> {
    let payload = serde_json::to_string(result)?;
    let cstr = CString::new(payload)?;
    unsafe {
        callback(cstr.as_ptr(), user_data as *mut c_void);
    }
    Ok(())
}

#[async_trait]
impl Reaction for CsharpReaction {
    fn id(&self) -> &str {
        self.base.get_id()
    }

    fn type_name(&self) -> &str {
        CSHARP_COMPONENT_TYPE
    }

    fn properties(&self) -> HashMap<String, Value> {
        HashMap::new()
    }

    fn query_ids(&self) -> Vec<String> {
        self.base.get_queries().to_vec()
    }

    fn auto_start(&self) -> bool {
        self.base.get_auto_start()
    }

    async fn initialize(&self, context: ReactionRuntimeContext) {
        self.base.initialize(context).await;
    }

    async fn start(&self) -> Result<()> {
        let shutdown_rx = self.base.create_shutdown_channel().await;
        let checkpoints = self.base.read_all_checkpoints().await.unwrap_or_default();
        let base = self.base.clone_shared();
        let callback = self.callback;
        let user_data = self.user_data;

        let task = tokio::spawn(async move {
            let result = base
                .run_standard_loop(shutdown_rx, checkpoints, move |event| async move {
                    dispatch(callback, user_data, &event)
                })
                .await;
            if let Err(err) = result {
                log::error!("csharp reaction loop stopped: {err:#}");
            }
        });

        self.base.set_processing_task(task).await;
        self.base.set_status(ComponentStatus::Running, None).await;
        Ok(())
    }

    async fn stop(&self) -> Result<()> {
        self.base.stop_common().await
    }

    async fn status(&self) -> ComponentStatus {
        self.base.get_status().await
    }

    async fn enqueue_query_result(&self, result: QueryResult) -> Result<()> {
        self.base.enqueue_query_result(result).await
    }
}
