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
use drasi_core::models::{Element, SourceChange};
use drasi_lib::bootstrap::{
    BootstrapContext, BootstrapProvider, BootstrapRequest, BootstrapResult,
};
use drasi_lib::channels::{BootstrapEvent, BootstrapEventSender, QueryResult, SubscriptionResponse};
use drasi_lib::config::SourceSubscriptionSettings;
use drasi_lib::context::{ReactionRuntimeContext, SourceRuntimeContext};
use drasi_lib::{
    ComponentStatus, Reaction, ReactionBase, ReactionBaseParams, ReactionRecoveryPolicy, Source,
    SourceBase, SourceBaseParams,
};
use serde_json::Value;
use tokio::sync::Mutex as TokioMutex;

const CSHARP_COMPONENT_TYPE: &str = "csharp";

/// Returns 0 on success. A non-zero status fails a durable checkpoint.
pub type ReactionCallback = unsafe extern "C" fn(*const c_char, *mut c_void) -> i32;

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
    /// Live elements, replayed to late-joining queries via bootstrap.
    snapshot: Arc<TokioMutex<HashMap<String, Element>>>,
}

impl CsharpSource {
    pub fn new(id: &str, auto_start: bool) -> Result<Self> {
        let params = SourceBaseParams::new(id).with_auto_start(auto_start);
        Ok(Self {
            base: SourceBase::new(params)?,
            dispatch: TokioMutex::new(()),
            snapshot: Arc::new(TokioMutex::new(HashMap::new())),
        })
    }

    pub async fn push(&self, change: SourceChange) -> Result<()> {
        let _ordered = self.dispatch.lock().await;
        {
            let mut snapshot = self.snapshot.lock().await;
            match &change {
                SourceChange::Insert { element } | SourceChange::Update { element } => {
                    snapshot.insert(element_id(element), element.clone());
                }
                SourceChange::Delete { metadata } => {
                    snapshot.remove(metadata.reference.element_id.as_ref());
                }
                _ => {}
            }
        }
        self.base.dispatch_source_change(change).await
    }
}

fn element_id(element: &Element) -> String {
    match element {
        Element::Node { metadata, .. } | Element::Relation { metadata, .. } => {
            metadata.reference.element_id.to_string()
        }
    }
}

struct SnapshotBootstrap {
    source_id: String,
    snapshot: Arc<TokioMutex<HashMap<String, Element>>>,
}

fn labels_match(metadata_labels: &[Arc<str>], requested: &[String]) -> bool {
    requested.is_empty()
        || metadata_labels
            .iter()
            .any(|label| requested.iter().any(|want| want == label.as_ref()))
}

#[async_trait]
impl BootstrapProvider for SnapshotBootstrap {
    async fn bootstrap(
        &self,
        request: BootstrapRequest,
        context: &BootstrapContext,
        event_tx: BootstrapEventSender,
        _settings: Option<&SourceSubscriptionSettings>,
    ) -> Result<BootstrapResult> {
        let snapshot = self.snapshot.lock().await;
        let mut count = 0;
        for element in snapshot.values() {
            let metadata = match element {
                Element::Node { metadata, .. } => metadata,
                Element::Relation { metadata, .. } => metadata,
            };
            let wanted = match element {
                Element::Node { .. } => labels_match(&metadata.labels, &request.node_labels),
                Element::Relation { .. } => {
                    labels_match(&metadata.labels, &request.relation_labels)
                }
            };
            if !wanted {
                continue;
            }
            event_tx
                .send(BootstrapEvent {
                    source_id: self.source_id.clone(),
                    change: SourceChange::Insert {
                        element: element.clone(),
                    },
                    timestamp: chrono::Utc::now(),
                    sequence: context.next_sequence(),
                })
                .await?;
            count += 1;
        }
        Ok(BootstrapResult {
            event_count: count,
            source_position: None,
        })
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
        // Persistent indexes (RocksDB) require replay-capable sources.
        // In-process pushes are the bootstrap; the index is the durable copy.
        true
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
        self.base
            .set_bootstrap_provider(SnapshotBootstrap {
                source_id: self.base.get_id().to_string(),
                snapshot: Arc::clone(&self.snapshot),
            })
            .await;
    }

    async fn set_bootstrap_provider(
        &self,
        provider: Box<dyn BootstrapProvider + 'static>,
    ) {
        self.base.set_bootstrap_provider(provider).await;
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

    async fn set_bootstrap_provider(
        &self,
        provider: Box<dyn BootstrapProvider + 'static>,
    ) {
        self.0.set_bootstrap_provider(provider).await;
    }
}

/// A reaction that forwards each query result to a managed callback as JSON.
pub struct CsharpReaction {
    base: ReactionBase,
    callback: ReactionCallback,
    user_data: usize,
    durable: bool,
}

impl CsharpReaction {
    pub fn new(
        id: &str,
        query_ids: Vec<String>,
        callback: ReactionCallback,
        user_data: *mut c_void,
        auto_start: bool,
    ) -> Self {
        Self::build(id, query_ids, callback, user_data, auto_start, false, None)
    }

    pub fn durable(
        id: &str,
        query_ids: Vec<String>,
        callback: ReactionCallback,
        user_data: *mut c_void,
        recovery: ReactionRecoveryPolicy,
    ) -> Self {
        Self::build(
            id,
            query_ids,
            callback,
            user_data,
            true,
            true,
            Some(recovery),
        )
    }

    fn build(
        id: &str,
        query_ids: Vec<String>,
        callback: ReactionCallback,
        user_data: *mut c_void,
        auto_start: bool,
        durable: bool,
        recovery: Option<ReactionRecoveryPolicy>,
    ) -> Self {
        let mut params = ReactionBaseParams::new(id, query_ids).with_auto_start(auto_start);
        if let Some(recovery) = recovery {
            params = params.with_recovery_policy(recovery);
        }
        Self {
            base: ReactionBase::new(params),
            callback,
            user_data: user_data as usize,
            durable,
        }
    }
}

fn dispatch(callback: ReactionCallback, user_data: usize, result: &QueryResult) -> Result<()> {
    let payload = serde_json::to_string(result)?;
    let cstr = CString::new(payload)?;
    let status = unsafe { callback(cstr.as_ptr(), user_data as *mut c_void) };
    if status != 0 {
        anyhow::bail!("csharp reaction callback failed");
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

    fn is_durable(&self) -> bool {
        self.durable
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

/// Adapts a `Box<dyn Source>` produced by a plugin descriptor.
pub struct BoxedSource(pub Box<dyn Source>);

#[async_trait]
impl Source for BoxedSource {
    fn id(&self) -> &str {
        self.0.id()
    }
    fn type_name(&self) -> &str {
        self.0.type_name()
    }
    fn properties(&self) -> HashMap<String, Value> {
        self.0.properties()
    }
    fn dispatch_mode(&self) -> drasi_lib::DispatchMode {
        self.0.dispatch_mode()
    }
    fn auto_start(&self) -> bool {
        self.0.auto_start()
    }
    fn supports_replay(&self) -> bool {
        self.0.supports_replay()
    }
    fn describe_schema(&self) -> Option<drasi_lib::schema::SourceSchema> {
        self.0.describe_schema()
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
    async fn deprovision(&self) -> Result<()> {
        self.0.deprovision().await
    }
    async fn initialize(&self, context: SourceRuntimeContext) {
        self.0.initialize(context).await;
    }
    async fn set_bootstrap_provider(&self, provider: Box<dyn BootstrapProvider + 'static>) {
        self.0.set_bootstrap_provider(provider).await;
    }
}

/// Adapts a `Box<dyn Reaction>` produced by a plugin descriptor.
pub struct BoxedReaction(pub Box<dyn Reaction>);

#[async_trait]
impl Reaction for BoxedReaction {
    fn id(&self) -> &str {
        self.0.id()
    }
    fn type_name(&self) -> &str {
        self.0.type_name()
    }
    fn properties(&self) -> HashMap<String, Value> {
        self.0.properties()
    }
    fn query_ids(&self) -> Vec<String> {
        self.0.query_ids()
    }
    fn auto_start(&self) -> bool {
        self.0.auto_start()
    }
    fn is_durable(&self) -> bool {
        self.0.is_durable()
    }
    fn needs_snapshot_on_fresh_start(&self) -> bool {
        self.0.needs_snapshot_on_fresh_start()
    }
    async fn initialize(&self, context: ReactionRuntimeContext) {
        self.0.initialize(context).await;
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
    async fn enqueue_query_result(&self, result: QueryResult) -> Result<()> {
        self.0.enqueue_query_result(result).await
    }
    async fn deprovision(&self) -> Result<()> {
        self.0.deprovision().await
    }
}
