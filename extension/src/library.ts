import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { LspLocation, openLocation } from "./infoView";

/** The language server's requests for the standard library's modules and exports (see `LibraryHandlers` in the compiler). */
const MODULES_REQUEST = "nanyx/libraryModules";
const MODULE_REQUEST = "nanyx/libraryModule";
const ADD_IMPORT_REQUEST = "nanyx/addImport";
const SEARCH_REQUEST = "nanyx/librarySearch";

export const COMMAND_OPEN_EXPORT = "nanyx.library.open";
export const COMMAND_ADD_IMPORT = "nanyx.library.addImport";
export const COMMAND_SEARCH = "nanyx.library.search";

interface LibraryModuleItem {
  name: string;
  documentation?: string;
}

interface LibraryExportItem {
  name: string;
  kind: "function" | "value" | "type" | "context" | "attached";
  attachedTo?: string;
  signature: string;
  documentation?: string;
  import?: string;
  location?: LspLocation;
}

/** A match of `nanyx/librarySearch`: an export, or a member of a context the library exports (`ToString(a).toString`). */
interface LibrarySearchItem {
  name: string;
  signature: string;
  documentation?: string;
  import?: string;
  location?: LspLocation;
  needs: string[];
}

interface LibrarySearchResult {
  matches: LibrarySearchItem[];
  error?: string;
}

interface SearchPickItem extends vscode.QuickPickItem {
  found?: LibrarySearchItem;
}

interface LspTextEdit {
  range: { start: { line: number; character: number }; end: { line: number; character: number } };
  newText: string;
}

/** A node of the tree: a module, a group of its exports, or an export. */
type LibraryNode =
  | { kind: "module"; module: LibraryModuleItem }
  | { kind: "group"; module: string; label: string; exports: LibraryExportItem[] }
  | { kind: "export"; module: string; export: LibraryExportItem };

/** The groups a module's exports are shown in, in this order, with attached definitions after them by their type. */
const GROUPS: [LibraryExportItem["kind"], string][] = [
  ["function", "Functions"],
  ["value", "Values"],
  ["type", "Types"],
  ["context", "Contexts"]
];

/**
 * The *Library* view: the standard library's modules, the prelude first, each with what it exports grouped by kind,
 * attached definitions under the type they attach to. An export opens its declaration in the library's source, and
 * its button imports its module into the active document.
 */
export class LibraryView implements vscode.TreeDataProvider<LibraryNode>, vscode.Disposable {
  static readonly viewId = "nanyx.library";

  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly registrations: vscode.Disposable[];
  /** Each module's exports, asked for once per server. */
  private readonly exports = new Map<string, Promise<LibraryExportItem[]>>();

  constructor(private readonly client: () => LanguageClient | undefined) {
    this.registrations = [
      vscode.window.registerTreeDataProvider(LibraryView.viewId, this),
      vscode.commands.registerCommand(COMMAND_OPEN_EXPORT, (location: LspLocation) => openLocation(location)),
      vscode.commands.registerCommand(COMMAND_ADD_IMPORT, (node: LibraryNode) => {
        if (node.kind === "export") {
          void this.addImport(node.module);
        }
      }),
      vscode.commands.registerCommand(COMMAND_SEARCH, () => this.search())
    ];
  }

  /** Lists the library again, as the server now running has it. */
  refresh(): void {
    this.exports.clear();
    this.changed.fire();
  }

  dispose(): void {
    this.registrations.forEach((registration) => registration.dispose());
    this.changed.dispose();
  }

  async getChildren(node?: LibraryNode): Promise<LibraryNode[]> {
    const client = this.client();
    if (!client) {
      return [];
    }

    if (!node) {
      const modules = await client.sendRequest<LibraryModuleItem[]>(MODULES_REQUEST, {});
      return modules.map((module) => ({ kind: "module", module }));
    }

    if (node.kind === "module") {
      return groupsOf(node.module.name, await this.exportsOf(client, node.module.name));
    }

    if (node.kind === "group") {
      return node.exports.map((item) => ({ kind: "export", module: node.module, export: item }));
    }

    return [];
  }

  getTreeItem(node: LibraryNode): vscode.TreeItem {
    switch (node.kind) {
      case "module": {
        const item = new vscode.TreeItem(node.module.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = summaryOf(node.module.documentation);
        item.tooltip = node.module.documentation ? new vscode.MarkdownString(node.module.documentation) : undefined;
        item.iconPath = new vscode.ThemeIcon("package");
        return item;
      }
      case "group": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = `${node.exports.length}`;
        return item;
      }
      case "export": {
        const exported = node.export;
        const item = new vscode.TreeItem(exported.name, vscode.TreeItemCollapsibleState.None);
        item.description = exported.signature.replace(/\s+/g, " ");
        item.tooltip = tooltipOf(exported);
        item.iconPath = new vscode.ThemeIcon(iconOf(exported.kind));
        // Only an export with an import to add has the button.
        item.contextValue = exported.import ? "importable" : "export";
        if (exported.location) {
          item.command = { title: "Open", command: COMMAND_OPEN_EXPORT, arguments: [exported.location] };
        }
        return item;
      }
    }
  }

  private exportsOf(client: LanguageClient, module: string): Promise<LibraryExportItem[]> {
    let exports = this.exports.get(module);
    if (!exports) {
      exports = client.sendRequest<LibraryExportItem[] | null>(MODULE_REQUEST, { name: module }).then((found) => found ?? []);
      this.exports.set(module, exports);
    }

    return exports;
  }

  /**
   * *Search library by type*: a quick pick that searches the library by name, by type or both as you type, and inserts
   * the chosen name at the cursor with the import it needs.
   */
  private async search(): Promise<void> {
    const client = this.client();
    if (!client) {
      void vscode.window.showWarningMessage("Nanyx: the language server is not running.");
      return;
    }

    const editor = vscode.window.activeTextEditor;
    const pick = vscode.window.createQuickPick<SearchPickItem>();
    pick.placeholder = "A name, a type (bool -> string), or both (trim: string -> string)";
    pick.matchOnDescription = true;
    pick.matchOnDetail = true;
    let timer: NodeJS.Timeout | undefined;
    let latest = 0;
    pick.onDidChangeValue((query) => {
      clearTimeout(timer);
      timer = setTimeout(async () => {
        const asked = ++latest;
        if (query.trim() === "") {
          pick.items = [];
          return;
        }

        pick.busy = true;
        const result = await client.sendRequest<LibrarySearchResult>(SEARCH_REQUEST, { query });
        // An answer a later query has overtaken is dropped.
        if (asked !== latest) {
          return;
        }

        pick.busy = false;
        // VS Code filters the items by the text typed, which a query by type never contains, so each item is
        // always shown.
        pick.items = result.error
          ? [{ label: result.error, alwaysShow: true }]
          : result.matches.map((found) => ({
              label: found.name,
              description: found.signature.replace(/\s+/g, " "),
              detail: detailOf(found),
              alwaysShow: true,
              found
            }));
      }, 200);
    });
    pick.onDidAccept(async () => {
      const found = pick.selectedItems[0]?.found;
      pick.hide();
      if (!found) {
        return;
      }

      if (!editor || editor.document.languageId !== "nanyx") {
        if (found.location) {
          await openLocation(found.location);
        }
        return;
      }

      await editor.edit((builder) => editor.selections.forEach((selection) => builder.replace(selection, insertedName(found.name))));
      if (found.import) {
        await this.addImport(found.import.replace(/^import\s+/, ""), editor);
      }
    });
    pick.onDidHide(() => {
      clearTimeout(timer);
      pick.dispose();
    });
    pick.show();
  }

  /** Imports a module into a Nanyx document, the active one by default, where the missing-import quick fix would put it. */
  private async addImport(module: string, editor = vscode.window.activeTextEditor): Promise<void> {
    const client = this.client();
    if (!client) {
      return;
    }

    if (!editor || editor.document.languageId !== "nanyx") {
      void vscode.window.showWarningMessage("Nanyx: open a .nyx file to add the import to.");
      return;
    }

    const edits = await client.sendRequest<LspTextEdit[] | null>(ADD_IMPORT_REQUEST, {
      textDocument: { uri: client.code2ProtocolConverter.asUri(editor.document.uri) },
      module
    });
    if (edits === null) {
      void vscode.window.showWarningMessage(`Nanyx: ${editor.document.fileName} does not parse, so the import was not added.`);
      return;
    }

    if (edits.length === 0) {
      vscode.window.setStatusBarMessage(`Nanyx: ${module} is already imported`, 3000);
      return;
    }

    const workspaceEdit = new vscode.WorkspaceEdit();
    for (const edit of edits) {
      const { start, end } = edit.range;
      workspaceEdit.replace(editor.document.uri, new vscode.Range(start.line, start.character, end.line, end.character), edit.newText);
    }
    await vscode.workspace.applyEdit(workspaceEdit);
  }
}

/** A module's exports in groups: by kind, then the attached definitions by the type they attach to. Empty groups are left out. */
function groupsOf(module: string, exports: LibraryExportItem[]): LibraryNode[] {
  const byKind: LibraryNode[] = GROUPS.map(([kind, label]) => ({
    kind: "group" as const,
    module,
    label,
    exports: exports.filter((item) => item.kind === kind)
  }));
  const attachedTo = [...new Set(exports.filter((item) => item.kind === "attached").map((item) => item.attachedTo ?? ""))].sort();
  const attached: LibraryNode[] = attachedTo.map((type) => ({
    kind: "group" as const,
    module,
    label: type,
    exports: exports.filter((item) => item.kind === "attached" && item.attachedTo === type)
  }));
  return [...byKind, ...attached].filter((group) => group.kind === "group" && group.exports.length > 0);
}

/** The first line of a doc comment, without its links' targets, for a module's line in the tree. */
function summaryOf(documentation: string | undefined): string | undefined {
  const firstLine = documentation?.split("\n").find((line) => line.trim() !== "");
  return firstLine?.replace(/\[`([^`]+)`\]\([^)]*\)/g, "$1").replace(/`/g, "");
}

/** An export's signature, doc comment and the import it needs, as hover shows them. */
function tooltipOf(exported: LibraryExportItem): vscode.MarkdownString {
  const tooltip = new vscode.MarkdownString();
  tooltip.appendCodeblock(exported.signature, "nanyx");
  if (exported.documentation) {
    tooltip.appendMarkdown(`\n\n${exported.documentation}`);
  }
  if (exported.import) {
    tooltip.appendMarkdown("\n\n");
    tooltip.appendCodeblock(exported.import, "nanyx");
  }
  return tooltip;
}

/** A match's import and the contexts it still needs, for its second line in the quick pick. */
function detailOf(found: LibrarySearchItem): string | undefined {
  const parts = [found.import, found.needs.length > 0 ? `needs ${found.needs.join(", ")}` : undefined].filter((part) => part);
  return parts.length > 0 ? parts.join(" · ") : undefined;
}

/** The name source writes for a match: a context's member by its own name (`toString`), anything else as listed. */
function insertedName(name: string): string {
  const member = name.lastIndexOf(").");
  return member >= 0 ? name.substring(member + 2) : name;
}

function iconOf(kind: LibraryExportItem["kind"]): string {
  switch (kind) {
    case "function":
    case "attached":
      return "symbol-function";
    case "value":
      return "symbol-constant";
    case "type":
      return "symbol-class";
    case "context":
      return "symbol-interface";
  }
}
