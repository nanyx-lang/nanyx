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
exports.LibraryView = exports.COMMAND_ADD_IMPORT = exports.COMMAND_OPEN_EXPORT = void 0;
const vscode = __importStar(require("vscode"));
const infoView_1 = require("./infoView");
/** The language server's requests for the standard library's modules and exports (see `LibraryHandlers` in the compiler). */
const MODULES_REQUEST = "nanyx/libraryModules";
const MODULE_REQUEST = "nanyx/libraryModule";
const ADD_IMPORT_REQUEST = "nanyx/addImport";
exports.COMMAND_OPEN_EXPORT = "nanyx.library.open";
exports.COMMAND_ADD_IMPORT = "nanyx.library.addImport";
/** The groups a module's exports are shown in, in this order, with attached definitions after them by their type. */
const GROUPS = [
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
class LibraryView {
    constructor(client) {
        this.client = client;
        this.changed = new vscode.EventEmitter();
        this.onDidChangeTreeData = this.changed.event;
        /** Each module's exports, asked for once per server. */
        this.exports = new Map();
        this.registrations = [
            vscode.window.registerTreeDataProvider(LibraryView.viewId, this),
            vscode.commands.registerCommand(exports.COMMAND_OPEN_EXPORT, (location) => (0, infoView_1.openLocation)(location)),
            vscode.commands.registerCommand(exports.COMMAND_ADD_IMPORT, (node) => this.addImport(node))
        ];
    }
    /** Lists the library again, as the server now running has it. */
    refresh() {
        this.exports.clear();
        this.changed.fire();
    }
    dispose() {
        this.registrations.forEach((registration) => registration.dispose());
        this.changed.dispose();
    }
    async getChildren(node) {
        const client = this.client();
        if (!client) {
            return [];
        }
        if (!node) {
            const modules = await client.sendRequest(MODULES_REQUEST, {});
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
    getTreeItem(node) {
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
                    item.command = { title: "Open", command: exports.COMMAND_OPEN_EXPORT, arguments: [exported.location] };
                }
                return item;
            }
        }
    }
    exportsOf(client, module) {
        let exports = this.exports.get(module);
        if (!exports) {
            exports = client.sendRequest(MODULE_REQUEST, { name: module }).then((found) => found ?? []);
            this.exports.set(module, exports);
        }
        return exports;
    }
    /** Imports an export's module into the active Nanyx document, where the missing-import quick fix would put it. */
    async addImport(node) {
        const client = this.client();
        const editor = vscode.window.activeTextEditor;
        if (node.kind !== "export" || !client) {
            return;
        }
        if (!editor || editor.document.languageId !== "nanyx") {
            void vscode.window.showWarningMessage("Nanyx: open a .nyx file to add the import to.");
            return;
        }
        const edits = await client.sendRequest(ADD_IMPORT_REQUEST, {
            textDocument: { uri: client.code2ProtocolConverter.asUri(editor.document.uri) },
            module: node.module
        });
        if (edits === null) {
            void vscode.window.showWarningMessage(`Nanyx: ${editor.document.fileName} does not parse, so the import was not added.`);
            return;
        }
        if (edits.length === 0) {
            vscode.window.setStatusBarMessage(`Nanyx: ${node.module} is already imported`, 3000);
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
exports.LibraryView = LibraryView;
LibraryView.viewId = "nanyx.library";
/** A module's exports in groups: by kind, then the attached definitions by the type they attach to. Empty groups are left out. */
function groupsOf(module, exports) {
    const byKind = GROUPS.map(([kind, label]) => ({
        kind: "group",
        module,
        label,
        exports: exports.filter((item) => item.kind === kind)
    }));
    const attachedTo = [...new Set(exports.filter((item) => item.kind === "attached").map((item) => item.attachedTo ?? ""))].sort();
    const attached = attachedTo.map((type) => ({
        kind: "group",
        module,
        label: type,
        exports: exports.filter((item) => item.kind === "attached" && item.attachedTo === type)
    }));
    return [...byKind, ...attached].filter((group) => group.kind === "group" && group.exports.length > 0);
}
/** The first line of a doc comment, without its links' targets, for a module's line in the tree. */
function summaryOf(documentation) {
    const firstLine = documentation?.split("\n").find((line) => line.trim() !== "");
    return firstLine?.replace(/\[`([^`]+)`\]\([^)]*\)/g, "$1").replace(/`/g, "");
}
/** An export's signature, doc comment and the import it needs, as hover shows them. */
function tooltipOf(exported) {
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
function iconOf(kind) {
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
//# sourceMappingURL=library.js.map