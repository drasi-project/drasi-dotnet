# Drasi .NET documentation site

This directory contains the source for the **[Drasi .NET documentation
site](https://drasi-project.github.io/drasi-dotnet/)**, built with
[Hugo](https://gohugo.io/) and the [Docsy](https://www.docsy.dev/) theme and
published to GitHub Pages by
[`.github/workflows/website.yml`](../.github/workflows/website.yml).

## Layout

```
website/
├── config.toml          # Hugo + Docsy site configuration
├── content/             # Markdown content
│   ├── _index.md        #   landing page
│   └── docs/            #   getting-started, concepts, guides, api, examples
├── themes/docsy/        # Docsy theme (git submodule)
└── package.json         # PostCSS pipeline dependencies
```

## Prerequisites

- **Hugo extended**, v0.152.2 or newer (the theme's SCSS pipeline requires the
  *extended* build).
- **Node.js 18+**. Docsy pulls Bootstrap and Font Awesome from npm and runs its
  assets through PostCSS.
- The **Docsy submodule** must be checked out. If you cloned without
  `--recurse-submodules`:

  ```bash
  git submodule update --init --recursive
  ```

## Build and preview locally

From this `website/` directory:

```bash
# 1. Install the PostCSS pipeline and the theme's Bootstrap/Font Awesome assets.
npm install
npm --prefix themes/docsy install

# 2. Live-reloading preview at http://localhost:1313/drasi-dotnet/
hugo server

# 3. Production build into ./public
hugo --gc --minify
```

## Adding a page

Documentation pages live under `content/docs/`. Add a Markdown file (or an
`_index.md` for a new section) with Docsy front matter:

```yaml
---
title: "Page Title"
linkTitle: "Nav Title"
weight: 25          # controls sidebar ordering within the section
description: >
  One-line summary shown under the title and in listings.
---
```

`weight` orders items in the sidebar (lowest first). Cross-page links use relative
paths (for example `../api/#addqueryasync...`) so they resolve under the site's
`/drasi-dotnet/` base path on GitHub Pages.

## Theming

The site uses Drasi's brand theme, mirrored from the main
[`drasi-project/docs`](https://github.com/drasi-project/docs) site:

- `assets/scss/_variables_project.scss`. Brand colors (navy `#1f203f` + green
  `#75de6f`), typography, and design tokens (Docsy variable overrides).
- `assets/scss/_variables_project_after_bs.scss`. Custom Bootstrap theme colors.
- `assets/scss/_styles_project.scss`. Component and content styling (hero, flow
  diagram, cards, code blocks, syntax highlighting).
- `assets/icons/logo.svg`. The Drasi logo shown in the navbar.
- `layouts/_partials/hooks/head-end.html`. Loads the Inter and JetBrains Mono
  Google Fonts and defaults to light mode.
