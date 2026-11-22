"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.Highlighter = void 0;
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const jsonc = __importStar(require("jsonc-parser"));
const oniguruma = __importStar(require("vscode-oniguruma"));
const textmate = __importStar(require("vscode-textmate"));
/** The bits of a token's metadata from `tokenizeLine2` (vscode-textmate's `EncodedTokenAttributes`). */
const FONT_STYLE_MASK = 30720;
const FONT_STYLE_OFFSET = 11;
const FOREGROUND_MASK = 16744448;
const FOREGROUND_OFFSET = 15;
/** vscode-textmate's `FontStyle` flags. */
const ITALIC = 1;
const BOLD = 2;
const UNDERLINE = 4;
const STRIKETHROUGH = 8;
/**
 * The colour id vscode-textmate gives a token that no rule of the theme colours: the theme's default foreground, which
 * the view leaves to its own foreground colour, as the theme's editor foreground is not a token rule's.
 */
const DEFAULT_FOREGROUND = 1;
/** The scopes `editor.tokenColorCustomizations`'s short names stand for, as VS Code reads them. */
const CUSTOMIZATION_SCOPES = {
    comments: ["comment", "punctuation.definition.comment"],
    strings: ["string", "meta.embedded.assembly"],
    keywords: ["keyword", "keyword.control", "storage", "storage.type"],
    numbers: ["constant.numeric"],
    types: ["entity.name.type", "entity.name.class", "support.type", "support.class"],
    functions: ["entity.name.function", "support.function"],
    variables: ["variable", "entity.name.variable"]
};
/**
 * Highlights Nanyx code as the editor does: with the extension's TextMate grammar (`syntaxes/nanyx.tmLanguage.json`),
 * through vscode-textmate and vscode-oniguruma, the libraries VS Code's own highlighter is built on, and the token
 * colours of the current colour theme, read from the file of the extension that contributes it, with the user's
 * `editor.tokenColorCustomizations` on top. The theme is read again after `themeChanged`.
 */
class Highlighter {
    constructor(extensionPath) {
        this.extensionPath = extensionPath;
    }
    /** Forgets the theme's colours, to read them again for the next highlight: after the theme or its customisations change. */
    themeChanged() {
        this.colorMap = undefined;
    }
    /**
     * `code` highlighted, line by line, a line's state carried to the next; or, if the grammar can't be loaded, as it is,
     * in the view's own colour.
     */
    async highlight(code) {
        let loaded;
        try {
            loaded = await (this.registry ?? (this.registry = this.load()));
        }
        catch {
            return code.split("\n").map((line) => [{ text: line }]);
        }
        const { registry, grammar } = loaded;
        if (!this.colorMap) {
            registry.setTheme({ settings: themeRules() });
            this.colorMap = registry.getColorMap();
        }
        const colorMap = this.colorMap;
        let state = textmate.INITIAL;
        return code.split("\n").map((line) => {
            const result = grammar.tokenizeLine2(line, state);
            state = result.ruleStack;
            const tokens = [];
            for (let index = 0; index < result.tokens.length; index += 2) {
                const start = result.tokens[index];
                const end = index + 2 < result.tokens.length ? result.tokens[index + 2] : line.length;
                if (start < end) {
                    tokens.push(token(line.slice(start, end), result.tokens[index + 1], colorMap));
                }
            }
            return tokens;
        });
    }
    async load() {
        const wasm = await fs.promises.readFile(require.resolve("vscode-oniguruma/release/onig.wasm"));
        await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
        const grammarPath = path.join(this.extensionPath, "syntaxes", "nanyx.tmLanguage.json");
        const registry = new textmate.Registry({
            onigLib: Promise.resolve({
                createOnigScanner: (patterns) => new oniguruma.OnigScanner(patterns),
                createOnigString: (text) => new oniguruma.OnigString(text)
            }),
            loadGrammar: async () => textmate.parseRawGrammar(await fs.promises.readFile(grammarPath, "utf8"), grammarPath)
        });
        const grammar = await registry.loadGrammar("source.nanyx");
        if (!grammar) {
            throw new Error(`The Nanyx grammar did not load from ${grammarPath}.`);
        }
        return { registry, grammar };
    }
}
exports.Highlighter = Highlighter;
function token(text, metadata, colorMap) {
    const foreground = (metadata & FOREGROUND_MASK) >>> FOREGROUND_OFFSET;
    const fontStyle = (metadata & FONT_STYLE_MASK) >>> FONT_STYLE_OFFSET;
    const result = { text };
    if (foreground !== DEFAULT_FOREGROUND && colorMap[foreground]) {
        result.color = colorMap[foreground];
    }
    if (fontStyle & ITALIC)
        result.italic = true;
    if (fontStyle & BOLD)
        result.bold = true;
    if (fontStyle & UNDERLINE)
        result.underline = true;
    if (fontStyle & STRIKETHROUGH)
        result.strikethrough = true;
    return result;
}
/** What VS Code's setting names as the current theme: the preferred theme of the OS's scheme, if it follows it. */
function currentThemeName() {
    const workbench = vscode.workspace.getConfiguration("workbench");
    const window = vscode.workspace.getConfiguration("window");
    const kind = vscode.window.activeColorTheme.kind;
    if (kind === vscode.ColorThemeKind.HighContrast || kind === vscode.ColorThemeKind.HighContrastLight) {
        if (window.get("autoDetectHighContrast")) {
            return workbench.get(kind === vscode.ColorThemeKind.HighContrast ? "preferredHighContrastColorTheme" : "preferredHighContrastLightColorTheme");
        }
    }
    else if (window.get("autoDetectColorScheme")) {
        return workbench.get(kind === vscode.ColorThemeKind.Dark ? "preferredDarkColorTheme" : "preferredLightColorTheme");
    }
    return workbench.get("colorTheme");
}
/**
 * The token rules of the current theme, then the user's customisations of them, which come later so that they win: the
 * theme is found among the themes the installed extensions contribute (VS Code's own are those of its built-in
 * extensions) by the id or label the setting names it by.
 */
function themeRules() {
    const name = currentThemeName();
    const rules = [];
    for (const extension of vscode.extensions.all) {
        const themes = extension.packageJSON?.contributes?.themes ?? [];
        const theme = themes.find((candidate) => candidate.path && (candidate.id === name || candidate.label === name));
        if (theme?.path) {
            rules.push(...themeFileRules(path.join(extension.extensionPath, theme.path), new Set()));
            break;
        }
    }
    rules.push(...customizedRules(name));
    return rules;
}
/** The token rules of a theme file, those of the theme it includes first. A theme of a `.tmTheme` file gives none. */
function themeFileRules(file, seen) {
    if (seen.has(file)) {
        return [];
    }
    seen.add(file);
    let theme;
    try {
        theme = jsonc.parse(fs.readFileSync(file, "utf8"));
    }
    catch {
        return [];
    }
    const included = typeof theme?.include === "string" ? themeFileRules(path.join(path.dirname(file), theme.include), seen) : [];
    return [...included, ...tokenRules(theme?.tokenColors)];
}
/** The rules of a theme's `tokenColors`, or of a customisation's `textMateRules`, that colour or style something. */
function tokenRules(value) {
    return Array.isArray(value) ? value.filter((rule) => rule && typeof rule === "object" && rule.settings) : [];
}
/**
 * The rules of `editor.tokenColorCustomizations`: those for every theme, then those for the theme `name` (its
 * `"[name]"` key); in each, the short names (`comments`, `keywords`) and then `textMateRules`.
 */
function customizedRules(name) {
    const customizations = vscode.workspace.getConfiguration("editor").get("tokenColorCustomizations") ?? {};
    const rulesOf = (block) => {
        if (!block || typeof block !== "object") {
            return [];
        }
        const shortNamed = Object.entries(CUSTOMIZATION_SCOPES).flatMap(([key, scope]) => {
            const value = block[key];
            const settings = typeof value === "string" ? { foreground: value } : value && typeof value === "object" ? value : undefined;
            return settings ? [{ scope, settings }] : [];
        });
        return [...shortNamed, ...tokenRules(block.textMateRules)];
    };
    return [...rulesOf(customizations), ...(name ? rulesOf(customizations[`[${name}]`]) : [])];
}
//# sourceMappingURL=highlighter.js.map