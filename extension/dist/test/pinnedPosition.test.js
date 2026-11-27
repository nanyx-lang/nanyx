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
const node_test_1 = require("node:test");
const assert = __importStar(require("node:assert/strict"));
const pinnedPosition_1 = require("../pinnedPosition");
// The pinned position is on `total` in `  def total = a + b`, line 3, character 6.
const pinned = { line: 3, character: 6 };
const at = (line, character) => ({ line, character });
(0, node_test_1.test)("a line inserted above moves the pinned position down with its text", () => {
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(1, 0), end: at(1, 0), text: "def added = 1\n" }), at(4, 6));
});
(0, node_test_1.test)("lines deleted above move it up", () => {
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(0, 0), end: at(2, 0), text: "" }), at(1, 6));
});
(0, node_test_1.test)("text inserted before it on its line moves it along the line", () => {
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(3, 2), end: at(3, 2), text: "rec " }), at(3, 10));
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(3, 6), end: at(3, 6), text: "grand" }), at(3, 11));
});
(0, node_test_1.test)("a line broken before it carries it to the new line", () => {
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(3, 2), end: at(3, 2), text: "\n  " }), at(4, 6));
});
(0, node_test_1.test)("an edit after it leaves it where it is", () => {
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(3, 12), end: at(3, 13), text: "+" }), pinned);
    assert.deepEqual((0, pinnedPosition_1.moved)(pinned, { start: at(9, 0), end: at(9, 0), text: "x\n" }), pinned);
});
(0, node_test_1.test)("an edit that replaces the text it is on unpins it", () => {
    assert.equal((0, pinnedPosition_1.moved)(pinned, { start: at(3, 2), end: at(3, 11), text: "" }), undefined);
    assert.equal((0, pinnedPosition_1.moved)(pinned, { start: at(3, 6), end: at(3, 11), text: "sum" }), undefined);
    assert.equal((0, pinnedPosition_1.moved)(pinned, { start: at(2, 0), end: at(4, 0), text: "" }), undefined);
});
(0, node_test_1.test)("several changes are applied in turn, as VS Code gives them", () => {
    const changes = [
        { start: at(5, 0), end: at(5, 0), text: "-- after\n" },
        { start: at(0, 0), end: at(0, 0), text: "-- one\n-- two\n" }
    ];
    assert.deepEqual((0, pinnedPosition_1.movedThrough)(pinned, changes), at(5, 6));
    assert.equal((0, pinnedPosition_1.movedThrough)(pinned, [{ start: at(3, 0), end: at(4, 0), text: "" }, changes[1]]), undefined);
});
//# sourceMappingURL=pinnedPosition.test.js.map