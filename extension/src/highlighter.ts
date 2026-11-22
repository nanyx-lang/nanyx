import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import * as jsonc from "jsonc-parser";
import * as oniguruma from "vscode-oniguruma";
import * as textmate from "vscode-textmate";

/** A run of code in one style: a colour from the theme, if a rule of it gives one, and its font style. */
export interface CodeToken {
  text: string;
  color?: string;
  bold?: boolean;
  italic?: boolean;
  underline?: boolean;
  strikethrough?: boolean;
}

/** A rule of a theme's `tokenColors`: the scopes it applies to and how it styles them. */
type ThemeRule = textmate.IRawTheme["settings"][number];

/** Code as highlighted: a list of tokens for each of its lines. */
export type HighlightedCode = CodeToken[][];

/** The bits of a token's metadata from `tokenizeLine2` (vscode-textmate's `EncodedTokenAttributes`). */
const FONT_STYLE_MASK = 0b0000_0000_0000_0000_0111_1000_0000_0000;
const FONT_STYLE_OFFSET = 11;
const FOREGROUND_MASK = 0b0000_0000_1111_1111_1000_0000_0000_0000;
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
const CUSTOMIZATION_SCOPES: Record<string, string[]> = {
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
export class Highlighter {
  private registry: Promise<{ registry: textmate.Registry; grammar: textmate.IGrammar }> | undefined;
  private colorMap: string[] | undefined;

  constructor(private readonly extensionPath: string) {}

  /** Forgets the theme's colours, to read them again for the next highlight: after the theme or its customisations change. */
  themeChanged(): void {
    this.colorMap = undefined;
  }

  /**
   * `code` highlighted, line by line, a line's state carried to the next; or, if the grammar can't be loaded, as it is,
   * in the view's own colour.
   */
  async highlight(code: string): Promise<HighlightedCode> {
    let loaded: { registry: textmate.Registry; grammar: textmate.IGrammar };
    try {
      loaded = await (this.registry ??= this.load());
    } catch {
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
      const tokens: CodeToken[] = [];
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

  private async load(): Promise<{ registry: textmate.Registry; grammar: textmate.IGrammar }> {
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

function token(text: string, metadata: number, colorMap: string[]): CodeToken {
  const foreground = (metadata & FOREGROUND_MASK) >>> FOREGROUND_OFFSET;
  const fontStyle = (metadata & FONT_STYLE_MASK) >>> FONT_STYLE_OFFSET;
  const result: CodeToken = { text };
  if (foreground !== DEFAULT_FOREGROUND && colorMap[foreground]) {
    result.color = colorMap[foreground];
  }
  if (fontStyle & ITALIC) result.italic = true;
  if (fontStyle & BOLD) result.bold = true;
  if (fontStyle & UNDERLINE) result.underline = true;
  if (fontStyle & STRIKETHROUGH) result.strikethrough = true;
  return result;
}

/** What VS Code's setting names as the current theme: the preferred theme of the OS's scheme, if it follows it. */
function currentThemeName(): string | undefined {
  const workbench = vscode.workspace.getConfiguration("workbench");
  const window = vscode.workspace.getConfiguration("window");
  const kind = vscode.window.activeColorTheme.kind;
  if (kind === vscode.ColorThemeKind.HighContrast || kind === vscode.ColorThemeKind.HighContrastLight) {
    if (window.get<boolean>("autoDetectHighContrast")) {
      return workbench.get<string>(kind === vscode.ColorThemeKind.HighContrast ? "preferredHighContrastColorTheme" : "preferredHighContrastLightColorTheme");
    }
  } else if (window.get<boolean>("autoDetectColorScheme")) {
    return workbench.get<string>(kind === vscode.ColorThemeKind.Dark ? "preferredDarkColorTheme" : "preferredLightColorTheme");
  }
  return workbench.get<string>("colorTheme");
}

/**
 * The token rules of the current theme, then the user's customisations of them, which come later so that they win: the
 * theme is found among the themes the installed extensions contribute (VS Code's own are those of its built-in
 * extensions) by the id or label the setting names it by.
 */
function themeRules(): ThemeRule[] {
  const name = currentThemeName();
  const rules: ThemeRule[] = [];
  for (const extension of vscode.extensions.all) {
    const themes: { id?: string; label?: string; path?: string }[] = extension.packageJSON?.contributes?.themes ?? [];
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
function themeFileRules(file: string, seen: Set<string>): ThemeRule[] {
  if (seen.has(file)) {
    return [];
  }
  seen.add(file);
  let theme: { include?: string; tokenColors?: unknown };
  try {
    theme = jsonc.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return [];
  }
  const included = typeof theme?.include === "string" ? themeFileRules(path.join(path.dirname(file), theme.include), seen) : [];
  return [...included, ...tokenRules(theme?.tokenColors)];
}

/** The rules of a theme's `tokenColors`, or of a customisation's `textMateRules`, that colour or style something. */
function tokenRules(value: unknown): ThemeRule[] {
  return Array.isArray(value) ? value.filter((rule) => rule && typeof rule === "object" && rule.settings) : [];
}

/**
 * The rules of `editor.tokenColorCustomizations`: those for every theme, then those for the theme `name` (its
 * `"[name]"` key); in each, the short names (`comments`, `keywords`) and then `textMateRules`.
 */
function customizedRules(name: string | undefined): ThemeRule[] {
  const customizations = vscode.workspace.getConfiguration("editor").get<Record<string, unknown>>("tokenColorCustomizations") ?? {};
  const rulesOf = (block: Record<string, unknown> | undefined): ThemeRule[] => {
    if (!block || typeof block !== "object") {
      return [];
    }
    const shortNamed = Object.entries(CUSTOMIZATION_SCOPES).flatMap(([key, scope]) => {
      const value = block[key];
      const settings = typeof value === "string" ? { foreground: value } : value && typeof value === "object" ? value as ThemeRule["settings"] : undefined;
      return settings ? [{ scope, settings }] : [];
    });
    return [...shortNamed, ...tokenRules(block.textMateRules)];
  };
  return [...rulesOf(customizations), ...(name ? rulesOf(customizations[`[${name}]`] as Record<string, unknown>) : [])];
}
