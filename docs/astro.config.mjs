// @ts-check
import { defineConfig } from "astro/config";
import react from "@astrojs/react";
import sitemap from "@astrojs/sitemap";
import { rehypeHeadingIds, unified } from "@astrojs/markdown-remark";
import { inkNight, nanyxLanguage, paperWhite } from "./site/shiki-themes.mjs";
import nominalTypes from "./src/content/stdlib/nominal-types.json";
import {
  rehypeDiagnosticCodeIds,
  rehypeHeadingAnchors,
  rehypeNominalTypes,
  rehypeShiftHeadings,
} from "./site/rehype-plugins.mjs";
import { sectionRedirects } from "./site/sections.mjs";
import { unpackPlaygroundCompiler } from "./site/playground-compiler.mjs";
import { rehypePlaygroundReload, remarkPlaygroundLinks } from "./site/playground-examples.mjs";

export default defineConfig({
  site: "https://nanyx.dev",
  trailingSlash: "never",
  // Fetch a page when its link is hovered, so the client router can swap it in at once.
  prefetch: true,
  build: {
    format: "file",
  },
  redirects: {
    ...sectionRedirects(),
    // Pages that have moved to another section.
    "/docs/advanced/reference-counting": "/docs/deep-dives/reference-counting",
    "/docs/advanced/associated-types-and-constraints": "/docs/advanced/contexts#associated-types",
    "/docs/work-in-progress/type-arguments-and-reflection": "/docs/deep-dives/type-descriptions",
    "/docs/work-in-progress/sized-numbers": "/docs/advanced/sized-numbers",
    "/docs/work-in-progress/printing-values": "/docs/advanced/contexts#tostringa",
  },
  integrations: [
    react(),
    sitemap(),
    {
      // The build unpacks the playground's compiler into public/ before anything reads it.
      name: "playground-compiler",
      hooks: {
        "astro:config:setup": async ({ command }) => {
          if (command === "build") await unpackPlaygroundCompiler();
        },
      },
    },
  ],
  markdown: {
    processor: unified({
      remarkPlugins: [remarkPlaygroundLinks],
      rehypePlugins: [
        rehypeShiftHeadings,
        rehypeDiagnosticCodeIds,
        rehypeHeadingIds,
        rehypeHeadingAnchors,
        rehypePlaygroundReload,
        [rehypeNominalTypes, nominalTypes],
      ],
    }),
    shikiConfig: {
      themes: {
        light: /** @type {any} */ (paperWhite),
        dark: /** @type {any} */ (inkNight),
      },
      langs: [/** @type {any} */ (nanyxLanguage)],
    },
  },
  vite: {
    server: {
      fs: {
        allow: [".."],
      },
    },
  },
});
