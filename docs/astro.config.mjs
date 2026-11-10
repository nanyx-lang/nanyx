// @ts-check
import { defineConfig } from "astro/config";
import react from "@astrojs/react";
import sitemap from "@astrojs/sitemap";
import { rehypeHeadingIds, unified } from "@astrojs/markdown-remark";
import nyxGrammar from "../extension/syntaxes/nanyx.tmLanguage.json";
import { inkNight, paperWhite } from "./site/shiki-themes.mjs";
import { rehypeHeadingAnchors, rehypeShiftHeadings } from "./site/rehype-plugins.mjs";
import { sectionRedirects } from "./site/sections.mjs";

export default defineConfig({
  site: "https://nanyx.dev",
  trailingSlash: "never",
  build: {
    format: "file",
  },
  redirects: {
    ...sectionRedirects(),
    // Pages that have moved to another section.
    "/docs/advanced/reference-counting": "/docs/foundations/reference-counting",
    "/docs/advanced/associated-types-and-constraints": "/docs/work-in-progress/type-members-and-reflection",
    "/docs/work-in-progress/type-arguments-and-reflection": "/docs/work-in-progress/type-members-and-reflection",
  },
  integrations: [react(), sitemap()],
  markdown: {
    processor: unified({
      rehypePlugins: [rehypeShiftHeadings, rehypeHeadingIds, rehypeHeadingAnchors],
    }),
    shikiConfig: {
      themes: {
        light: /** @type {any} */ (paperWhite),
        dark: /** @type {any} */ (inkNight),
      },
      langs: [
        /** @type {any} */ ({
          ...nyxGrammar,
          name: "nanyx",
          scopeName: nyxGrammar.scopeName ?? "source.nanyx",
          aliases: ["nyx"],
        }),
      ],
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
