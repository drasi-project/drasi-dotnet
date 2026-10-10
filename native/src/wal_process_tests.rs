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

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use drasi_lib::channels::ComponentStatus;
use drasi_lib::wal::CapacityPolicy;
use drasi_lib::DurabilityConfig;
use drasi_source_application::{ApplicationSource, ApplicationSourceConfig, PropertyMapBuilder};

use crate::engine;

const CHILD_TEST: &str = "wal_process_tests::wal_recovery_child";
const MODE_ENV: &str = "DRASI_WAL_TEST_MODE";
const PATH_ENV: &str = "DRASI_WAL_TEST_PATH";
const SOURCE_ID: &str = "orders";
const QUERY_ID: &str = "all-orders";
const CHANGE_ID: &str = "wal-only";

#[test]
fn wal_provider_recovers_across_processes() {
    let wal_path = TestWalPath::new();
    run_child("write", &wal_path.0);
    assert!(
        wal_path.0.join(format!("{SOURCE_ID}.redb")).is_file(),
        "writer did not create the source WAL"
    );
    run_child("read", &wal_path.0);
}

#[test]
fn shutdown_releases_wal_for_same_process_reopen() {
    let wal_path = TestWalPath::new();
    crate::runtime().block_on(async {
        let first = create_engine(&wal_path.0, false).await;
        engine::start(&first).await.expect("start first engine");
        let source_handle = add_durable_source(&first).await;
        append_change(&source_handle).await;
        engine::shutdown(&first)
            .await
            .expect("shutdown first engine");

        let second = create_engine(&wal_path.0, false).await;
        engine::start(&second).await.expect("start second engine");
        add_durable_source(&second).await;
        add_replay_query(&second).await;
        wait_for_replayed_change(&second).await;
        engine::shutdown(&second)
            .await
            .expect("shutdown second engine");

        drop(first);
    });
}

#[test]
#[ignore = "invoked by wal_provider_recovers_across_processes"]
fn wal_recovery_child() {
    let Ok(mode) = std::env::var(MODE_ENV) else {
        return;
    };
    let wal_path = PathBuf::from(std::env::var(PATH_ENV).expect("child WAL path"));
    crate::runtime().block_on(async {
        match mode.as_str() {
            "write" => write_phase(&wal_path).await,
            "read" => read_phase(&wal_path).await,
            other => panic!("unknown child mode '{other}'"),
        }
    });
}

async fn write_phase(wal_path: &Path) {
    let handle = create_engine(wal_path, false).await;
    engine::start(&handle).await.expect("start writer engine");
    let source_handle = add_durable_source(&handle).await;
    append_change(&source_handle).await;
    engine::shutdown(&handle)
        .await
        .expect("shutdown writer engine");
}

async fn read_phase(wal_path: &Path) {
    let handle = create_engine(wal_path, true).await;
    engine::start(&handle).await.expect("start reader engine");
    add_durable_source(&handle).await;
    add_replay_query(&handle).await;
    wait_for_replayed_change(&handle).await;
    engine::shutdown(&handle)
        .await
        .expect("shutdown reader engine");
}

async fn add_durable_source(
    handle: &engine::EngineHandle,
) -> drasi_source_application::ApplicationSourceHandle {
    let (source, _) = durable_source();
    let source_handle = source.get_handle();
    handle
        .inner
        .core
        .add_source(source)
        .await
        .expect("add durable source");
    wait_for_source(&handle).await;
    source_handle
}

async fn add_replay_query(handle: &engine::EngineHandle) {
    engine::add_query(
        &handle,
        QUERY_ID.to_string(),
        "MATCH (o:Order) RETURN o.id AS id".to_string(),
        r#"["orders"]"#,
        Some(r#"{"enableBootstrap":true}"#),
    )
    .await
    .expect("add reader query");
    engine::wait_for_query(&handle, QUERY_ID.to_string(), 30.0)
        .await
        .expect("wait for reader query");
}

async fn wait_for_replayed_change(handle: &engine::EngineHandle) {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        let json = engine::get_query_results(&handle, QUERY_ID.to_string())
            .await
            .expect("read query results");
        let rows: Vec<serde_json::Value> = serde_json::from_str(&json).expect("query results JSON");
        if rows
            .iter()
            .any(|row| row.get("id").and_then(serde_json::Value::as_str) == Some(CHANGE_ID))
        {
            break;
        }
        assert!(
            tokio::time::Instant::now() < deadline,
            "reader did not replay the WAL change; rows: {rows:?}"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

async fn append_change(source_handle: &drasi_source_application::ApplicationSourceHandle) {
    let properties = PropertyMapBuilder::new()
        .with_string("id", CHANGE_ID)
        .build();
    source_handle
        .send_node_insert(CHANGE_ID, vec!["Order"], properties)
        .await
        .expect("append change to WAL");
}

async fn create_engine(wal_path: &Path, snake_case: bool) -> engine::EngineHandle {
    let key = if snake_case {
        "wal_provider"
    } else {
        "walProvider"
    };
    let json = serde_json::json!({
        key: {
            "kind": "redb",
            "path": wal_path,
        }
    })
    .to_string();
    engine::create("wal-process-test".to_string(), Some(&json))
        .await
        .expect("create engine with WAL provider")
}

fn durable_source() -> (
    ApplicationSource,
    drasi_source_application::ApplicationSourceHandle,
) {
    ApplicationSource::new(
        SOURCE_ID,
        ApplicationSourceConfig {
            properties: HashMap::new(),
            durability: Some(DurabilityConfig {
                enabled: true,
                max_events: 128,
                capacity_policy: CapacityPolicy::RejectIncoming,
            }),
        },
    )
    .expect("create durable application source")
}

async fn wait_for_source(handle: &engine::EngineHandle) {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        let status = handle
            .inner
            .core
            .get_source_status(SOURCE_ID)
            .await
            .expect("source status");
        if status == ComponentStatus::Running {
            return;
        }
        assert!(
            tokio::time::Instant::now() < deadline,
            "source stayed {status:?}"
        );
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

fn run_child(mode: &str, wal_path: &Path) {
    let output = Command::new(std::env::current_exe().expect("current test executable"))
        .args([
            "--exact",
            CHILD_TEST,
            "--ignored",
            "--nocapture",
            "--test-threads=1",
        ])
        .env(MODE_ENV, mode)
        .env(PATH_ENV, wal_path)
        .output()
        .expect("run WAL child process");
    assert!(
        output.status.success(),
        "{mode} child failed\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

struct TestWalPath(PathBuf);

impl TestWalPath {
    fn new() -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time")
            .as_nanos();
        Self(std::env::temp_dir().join(format!("drasi-wal-{}-{nonce}", std::process::id())))
    }
}

impl Drop for TestWalPath {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
