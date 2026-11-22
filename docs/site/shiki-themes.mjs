// Shiki themes for the docs. These mirror the light and dark palettes used
// across the site and target the scopes emitted by the Nanyx TextMate grammar
// in ../../extension/syntaxes/nanyx.tmLanguage.json, which `nanyxLanguage` registers with Shiki. The docs' Markdown and
// the home page's `<Code>` blocks both highlight with these.

import nyxGrammar from "../../extension/syntaxes/nanyx.tmLanguage.json";

/** The Nanyx grammar, as Shiki takes a language: ```nanyx and ```nyx blocks use it. */
export const nanyxLanguage = {
  ...nyxGrammar,
  name: "nanyx",
  scopeName: nyxGrammar.scopeName ?? "source.nanyx",
  aliases: ["nyx"],
};

const punctuationScopes = [
  "punctuation.section",
  "punctuation.separator",
  "punctuation.accessor",
  "punctuation.definition.name",
  "punctuation.definition.nominal",
  "punctuation.definition.symbol",
  "punctuation.definition.annotation",
];

export const paperWhite = {
  name: "paper-white",
  type: "light",
  colors: {
    "editor.background": "#f7f5f2",
    "editor.foreground": "#2b2b2b",
  },
  settings: [
    { settings: { foreground: "#2b2b2b" } },
    { scope: ["comment", "punctuation.definition.comment"], settings: { foreground: "#7a7a7a" } },
    { scope: ["string", "punctuation.definition.string"], settings: { foreground: "#c94c12" } },
    { scope: ["meta.embedded.interpolation", "punctuation.section.interpolation"], settings: { foreground: "#2b2b2b" } },
    { scope: ["constant.numeric", "constant.language", "variable.language"], settings: { foreground: "#098658" } },
    { scope: ["keyword.control"], settings: { foreground: "#c244b1" } },
    { scope: ["keyword.declaration", "keyword.operator.logical"], settings: { foreground: "#2b579a" } },
    { scope: ["entity.other.attribute-name"], settings: { foreground: "#2b579a" } },
    { scope: ["entity.name.function", "support.function"], settings: { foreground: "#795e26" } },
    { scope: ["entity.name.type", "support.type"], settings: { foreground: "#23a87c" } },
    { scope: ["entity.name.namespace"], settings: { foreground: "#23a87c" } },
    { scope: ["variable", "variable.parameter", "variable.other.member"], settings: { foreground: "#001080" } },
    { scope: ["constant.other.symbol"], settings: { foreground: "#1b8a5a" } },
    { scope: ["constant.other.symbol.pattern"], settings: { foreground: "#448eca" } },
    { scope: punctuationScopes, settings: { foreground: "#4a4a4a" } },
    { scope: ["keyword.operator"], settings: { foreground: "#000000" } },
    { scope: ["keyword.operator.pipe"], settings: { foreground: "#c244b1" } },
  ],
};

export const inkNight = {
  name: "ink-night",
  type: "dark",
  colors: {
    "editor.background": "#111214",
    "editor.foreground": "#e6e6e6",
  },
  settings: [
    { settings: { foreground: "#e6e6e6" } },
    { scope: ["comment", "punctuation.definition.comment"], settings: { foreground: "#8b8f98" } },
    { scope: ["string", "punctuation.definition.string"], settings: { foreground: "#f28f6b" } },
    { scope: ["meta.embedded.interpolation", "punctuation.section.interpolation"], settings: { foreground: "#e6e6e6" } },
    { scope: ["constant.numeric", "constant.language", "variable.language"], settings: { foreground: "#55c99b" } },
    { scope: ["keyword.control"], settings: { foreground: "#f07ad8" } },
    { scope: ["keyword.declaration", "keyword.operator.logical"], settings: { foreground: "#82b1ff" } },
    { scope: ["entity.other.attribute-name"], settings: { foreground: "#82b1ff" } },
    { scope: ["entity.name.function", "support.function"], settings: { foreground: "#f4bf75" } },
    { scope: ["entity.name.type", "support.type"], settings: { foreground: "#6ee7b7" } },
    { scope: ["entity.name.namespace"], settings: { foreground: "#6ee7b7" } },
    { scope: ["variable", "variable.parameter", "variable.other.member"], settings: { foreground: "#b8c7ff" } },
    { scope: ["constant.other.symbol"], settings: { foreground: "#7dfce7" } },
    { scope: ["constant.other.symbol.pattern"], settings: { foreground: "#34d3ab" } },
    { scope: punctuationScopes, settings: { foreground: "#999797" } },
    { scope: ["keyword.operator"], settings: { foreground: "#999797" } },
    { scope: ["keyword.operator.pipe"], settings: { foreground: "#f07ad8" } },
  ],
};
