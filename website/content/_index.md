---
type: "docs"
title: "Drasi .NET"
linkTitle: "Drasi .NET"
no_list: true
hide_readingtime: true
description: "Embed the Drasi continuous-query engine in your .NET application"
cascade:
  - type: "docs"
---

<div class="hero-section hero-section--compact">
  <h1 class="hero-title">Embed Drasi in your .NET app</h1>
  <p class="hero-subtitle"><code>Drasi</code> runs Drasi's continuous-query engine <strong>in-process</strong> in .NET. No servers, no brokers, no Kubernetes. Push graph changes from C#, subscribe to result diffs with callbacks or <code>IAsyncEnumerable</code>, or load native Drasi plugins at runtime.</p>

  <div class="cta-group">
    <a href="docs/getting-started/" class="cta-button cta-button--primary">
      <i class="fas fa-rocket"></i>
      Get started
    </a>
    <a href="docs/concepts/" class="cta-button cta-button--secondary">
      <i class="fas fa-lightbulb"></i>
      Why Drasi?
    </a>
  </div>
</div>

## How Drasi works in .NET

<p class="section-intro">Install the NuGet package, create sources, continuous queries, and reactions in code, and handle changes as they happen. A Rust <code>cdylib</code> hosts the embeddable engine behind a C ABI. C# binds it with <code>[LibraryImport]</code> and wraps it in <code>Engine</code>. Prebuilt binaries ship for Windows, Linux, and macOS.</p>

<div class="flow-diagram">
  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-cube"></i>
    </div>
    <div class="flow-step__label">Install</div>
    <div class="flow-step__description">dotnet add package Drasi</div>
  </div>

  <div class="flow-arrow">
    <i class="fas fa-arrow-right"></i>
  </div>

  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-code"></i>
    </div>
    <div class="flow-step__label">Write code</div>
    <div class="flow-step__description">Create sources, queries, and reactions</div>
  </div>

  <div class="flow-arrow">
    <i class="fas fa-arrow-right"></i>
  </div>

  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-bolt"></i>
    </div>
    <div class="flow-step__label">React to change</div>
    <div class="flow-step__description">Handle result diffs in your app</div>
  </div>
</div>

Push graph changes from your application into a C# **source**, run a **continuous query** in Cypher or GQL, and receive the added, updated, and removed rows in a C# **reaction**. All of that stays inside your process. When you need to connect to real systems, load Drasi's native source and reaction plugins at runtime, or pull them from the `ghcr.io/drasi-project` OCI registry.

## When to use Drasi in .NET

`Drasi` fits a .NET application or service that needs precise change detection without a separate cluster:

- **Event-driven services.** React to data changes without polling. Each diff includes the before and after state.
- **ASP.NET Core and worker services.** Register the engine with `AddDrasi` and let the generic host start and stop it.
- **Real-time dashboards.** Stream live query results over your own channel (WebSocket, SignalR, gRPC).
- **In-app reactive logic.** Replace hand-wired events with declarative continuous queries over application state.

## Documentation

<p class="section-intro">Everything you need to build with <code>Drasi</code>, from a first continuous query to the full API.</p>

<div class="doc-cards">
  <a href="docs/getting-started/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-rocket"></i></div>
    <div class="doc-card__title">Getting started</div>
    <div class="doc-card__desc">Install the package and run your first continuous query.</div>
  </a>
  <a href="docs/concepts/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-lightbulb"></i></div>
    <div class="doc-card__title">Concepts</div>
    <div class="doc-card__desc">Sources, continuous queries, and reactions in the change-driven model.</div>
  </a>
  <a href="docs/guides/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-book"></i></div>
    <div class="doc-card__title">Guides</div>
    <div class="doc-card__desc">Hosting, plugins, streaming, configuration, and errors.</div>
  </a>
  <a href="docs/api/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-code"></i></div>
    <div class="doc-card__title">API reference</div>
    <div class="doc-card__desc">Every public method on <code>Engine</code>, grouped by area.</div>
  </a>
  <a href="docs/examples/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-flask"></i></div>
    <div class="doc-card__title">Examples</div>
    <div class="doc-card__desc">Runnable samples: Quickstart, Hosted, and Plugins.</div>
  </a>
</div>
