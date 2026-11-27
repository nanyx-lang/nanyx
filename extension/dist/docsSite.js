"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.isDocsLink = isDocsLink;
/** The docs site, nanyx.dev, whose pages the compiler links to, such as an error code's entry in the errors reference. */
const DOCS_SITE = "https://nanyx.dev/";
/** Whether a link is to a page of the docs site. */
function isDocsLink(href) {
    return href.startsWith(DOCS_SITE);
}
//# sourceMappingURL=docsSite.js.map