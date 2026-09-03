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

//! Forwards native `tracing` / `log` events to a C# `ILogger` when one is
//! registered. Without a callback, events still go to stderr (honouring
//! `RUST_LOG`, default `warn`).

use std::ffi::{CStr, CString};
use std::io::{self, Write};
use std::os::raw::{c_char, c_void};
use std::sync::{Mutex, OnceLock};

use tracing::{Event, Level, Subscriber};
use tracing_subscriber::fmt;
use tracing_subscriber::layer::{Context, Layer, SubscriberExt};
use tracing_subscriber::reload;
use tracing_subscriber::util::SubscriberInitExt;
use tracing_subscriber::EnvFilter;
use tracing_subscriber::Registry;

pub type LogCallback =
    unsafe extern "C" fn(i32, *const c_char, *const c_char, *mut c_void);

struct Sink {
    callback: LogCallback,
    user_data: usize,
}

static SINK: Mutex<Option<Sink>> = Mutex::new(None);
static FILTER: OnceLock<reload::Handle<EnvFilter, Registry>> = OnceLock::new();

struct MessageVisitor {
    message: String,
}

impl tracing::field::Visit for MessageVisitor {
    fn record_str(&mut self, field: &tracing::field::Field, value: &str) {
        if field.name() == "message" {
            self.message = value.to_string();
        }
    }

    fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
        if field.name() == "message" && self.message.is_empty() {
            self.message = format!("{value:?}");
            if self.message.starts_with('"') && self.message.ends_with('"') && self.message.len() >= 2
            {
                self.message = self.message[1..self.message.len() - 1].to_string();
            }
        }
    }
}

struct CallbackLayer;

impl<S: Subscriber> Layer<S> for CallbackLayer {
    fn on_event(&self, event: &Event<'_>, _ctx: Context<'_, S>) {
        let Ok(guard) = SINK.lock() else {
            return;
        };
        let Some(sink) = guard.as_ref() else {
            return;
        };
        let mut visitor = MessageVisitor {
            message: String::new(),
        };
        event.record(&mut visitor);
        let level = match *event.metadata().level() {
            Level::TRACE => 0,
            Level::DEBUG => 1,
            Level::INFO => 2,
            Level::WARN => 3,
            Level::ERROR => 4,
        };
        let Ok(target) = CString::new(event.metadata().target()) else {
            return;
        };
        let Ok(message) = CString::new(visitor.message.replace('\0', "")) else {
            return;
        };
        unsafe {
            (sink.callback)(
                level,
                target.as_ptr(),
                message.as_ptr(),
                sink.user_data as *mut c_void,
            );
        }
    }
}

struct DualWriter {
    discard: bool,
}

impl Write for DualWriter {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        if self.discard {
            Ok(buf.len())
        } else {
            io::stderr().write(buf)
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        if self.discard {
            Ok(())
        } else {
            io::stderr().flush()
        }
    }
}

struct QuietIfSink;

impl<'a> fmt::MakeWriter<'a> for QuietIfSink {
    type Writer = DualWriter;

    fn make_writer(&'a self) -> DualWriter {
        DualWriter {
            discard: SINK.lock().ok().and_then(|g| g.as_ref().map(|_| ())).is_some(),
        }
    }
}

pub fn init() {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        let env = EnvFilter::try_from_default_env()
            .unwrap_or_else(|_| EnvFilter::new("warn"));
        let (filter, handle) = reload::Layer::new(env);
        let _ = FILTER.set(handle);
        let _ = tracing_log::LogTracer::init();
        let _ = tracing_subscriber::registry()
            .with(filter)
            .with(fmt::layer().with_writer(QuietIfSink))
            .with(CallbackLayer)
            .try_init();
    });
}

/// `callback` is invoked from native worker threads. Null clears the sink.
#[no_mangle]
pub unsafe extern "C" fn drasi_set_log_callback(
    callback: Option<LogCallback>,
    user_data: *mut c_void,
) {
    init();
    if let Ok(mut sink) = SINK.lock() {
        *sink = callback.map(|callback| Sink {
            callback,
            user_data: user_data as usize,
        });
    }
}

/// Reloads the `tracing` filter (`RUST_LOG` syntax). Null is a no-op.
#[no_mangle]
pub unsafe extern "C" fn drasi_set_log_filter(filter: *const c_char) {
    init();
    if filter.is_null() {
        return;
    }
    let Ok(text) = CStr::from_ptr(filter).to_str() else {
        return;
    };
    if let Some(handle) = FILTER.get() {
        let _ = handle.reload(EnvFilter::new(text));
    }
}
