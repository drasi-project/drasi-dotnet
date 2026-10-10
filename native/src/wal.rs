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

use std::sync::Arc;

use async_trait::async_trait;
use drasi_core::models::SourceChange;
use drasi_lib::wal::{WalError, WalProvider, WriteAheadLogConfig};
use tokio::sync::RwLock;

pub struct ReleasableWalProvider {
    inner: RwLock<Option<Arc<dyn WalProvider>>>,
}

impl ReleasableWalProvider {
    pub fn new(provider: Arc<dyn WalProvider>) -> Self {
        Self {
            inner: RwLock::new(Some(provider)),
        }
    }

    pub async fn release(&self) {
        self.inner.write().await.take();
    }
}

fn released() -> WalError {
    WalError::StorageError("WAL provider has been released".to_string())
}

#[async_trait]
impl WalProvider for ReleasableWalProvider {
    async fn register(&self, source_id: &str, config: WriteAheadLogConfig) -> Result<(), WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .register(source_id, config)
            .await
    }

    async fn append(&self, source_id: &str, event: &SourceChange) -> Result<u64, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .append(source_id, event)
            .await
    }

    async fn read_from(
        &self,
        source_id: &str,
        sequence: u64,
    ) -> Result<Vec<(u64, SourceChange)>, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .read_from(source_id, sequence)
            .await
    }

    async fn prune_up_to(&self, source_id: &str, sequence: u64) -> Result<u64, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .prune_up_to(source_id, sequence)
            .await
    }

    async fn head_sequence(&self, source_id: &str) -> Result<u64, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .head_sequence(source_id)
            .await
    }

    async fn oldest_sequence(&self, source_id: &str) -> Result<Option<u64>, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .oldest_sequence(source_id)
            .await
    }

    async fn event_count(&self, source_id: &str) -> Result<u64, WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .event_count(source_id)
            .await
    }

    async fn delete_wal(&self, source_id: &str) -> Result<(), WalError> {
        let guard = self.inner.read().await;
        guard
            .as_ref()
            .ok_or_else(released)?
            .delete_wal(source_id)
            .await
    }
}
