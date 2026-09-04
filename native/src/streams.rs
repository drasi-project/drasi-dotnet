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

//! Lifecycle and log streams that pump into a C callback.

use std::ffi::CString;
use std::os::raw::c_void;

use futures::StreamExt;
use serde::Serialize;
use tokio::sync::broadcast;
use tokio::sync::oneshot;

use crate::components::ReactionCallback;

pub struct StreamHandle {
    stop: Option<oneshot::Sender<()>>,
}

impl StreamHandle {
    pub fn close(mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
    }
}

impl Drop for StreamHandle {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
    }
}

fn dispatch_json(callback: ReactionCallback, user_data: usize, json: &str) -> bool {
    match CString::new(json) {
        Ok(cstr) => unsafe {
            let _ = callback(cstr.as_ptr(), user_data as *mut c_void);
            true
        },
        Err(_) => true,
    }
}

fn dispatch_value<T: Serialize>(callback: ReactionCallback, user_data: usize, value: &T) -> bool {
    match serde_json::to_string(value) {
        Ok(json) => dispatch_json(callback, user_data, &json),
        Err(err) => {
            log::error!("could not serialise a stream item: {err}");
            true
        }
    }
}

/// Pumps a broadcast subscription (history first) into a C callback.
pub fn pump_broadcast<T>(
    history: Vec<T>,
    mut receiver: broadcast::Receiver<T>,
    callback: ReactionCallback,
    user_data: usize,
) -> StreamHandle
where
    T: Serialize + Clone + Send + Sync + 'static,
{
    let (stop, mut stop_rx) = oneshot::channel();
    tokio::spawn(async move {
        for item in history {
            if !dispatch_value(callback, user_data, &item) {
                return;
            }
        }
        loop {
            tokio::select! {
                _ = &mut stop_rx => return,
                msg = receiver.recv() => match msg {
                    Ok(item) => {
                        if !dispatch_value(callback, user_data, &item) {
                            return;
                        }
                    }
                    Err(broadcast::error::RecvError::Lagged(count)) => {
                        let json = format!("{{\"lagged\":{count}}}");
                        if !dispatch_json(callback, user_data, &json) {
                            return;
                        }
                    }
                    Err(broadcast::error::RecvError::Closed) => return,
                }
            }
        }
    });
    StreamHandle { stop: Some(stop) }
}

/// Pumps a `futures::Stream` into a C callback.
pub fn pump_stream<S, T>(stream: S, callback: ReactionCallback, user_data: usize) -> StreamHandle
where
    S: futures::Stream<Item = T> + Send + 'static,
    T: Serialize + Send + Sync + 'static,
{
    let (stop, mut stop_rx) = oneshot::channel();
    tokio::spawn(async move {
        let mut stream = std::pin::pin!(stream);
        loop {
            tokio::select! {
                _ = &mut stop_rx => return,
                item = stream.next() => match item {
                    Some(item) => {
                        if !dispatch_value(callback, user_data, &item) {
                            return;
                        }
                    }
                    None => return,
                }
            }
        }
    });
    StreamHandle { stop: Some(stop) }
}
