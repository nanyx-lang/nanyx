// The Examples section of the docs is generated from the `.nyx` files in the
// repository's `examples` directory, so those files stay the single source of
// truth. This module lists them, grouped by theme, with the title and
// one-line description each page shows. Plain JS so that both
// astro.config.mjs and the site code can import it.
import path from "node:path";
import { fileURLToPath } from "node:url";

export const EXAMPLES_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../examples");

export const EXAMPLES_SOURCE_URL = "https://github.com/nanyx-lang/nanyx/blob/main/examples";

/** Slug of the generated page that lists every example. */
export const EXAMPLES_OVERVIEW_SLUG = "all-examples";

/**
 * @typedef {{ file: string; slug: string; title: string; description: string }} Example
 * @typedef {{ title: string; description: string; examples: Example[] }} ExampleGroup
 */

/** @type {ExampleGroup[]} */
export const exampleGroups = [
  {
    title: "First steps",
    description: "Short programs that show the everyday shape of Nanyx code.",
    examples: [
      {
        file: "zipping.nyx",
        slug: "zipping",
        title: "Zipping in for loops",
        description: "A script module whose for loops walk several lists at once.",
      },
      {
        file: "fizzbuzzbaz.nyx",
        slug: "fizzbuzzbaz",
        title: "Fizzbuzzbaz",
        description: "Fizzbuzz with the rules supplied at runtime, built from pipes and a returned lambda.",
      },
      {
        file: "eightQueens.nyx",
        slug: "eight-queens",
        title: "Eight queens",
        description: "A recursive backtracking search with a local rec function and tagged results.",
      },
      {
        file: "stringTransformation.nyx",
        slug: "string-transformation",
        title: "String casing",
        description: "Convert between camel, pascal, kebab, snake, title and sentence case.",
      },
      {
        file: "regex-matching.nyx",
        slug: "regex-matching",
        title: "Regex matching with custom patterns",
        description: "Custom patterns that wrap regular expressions so a match reads like a literal.",
      },
      {
        file: "semanticVersionParsing.nyx",
        slug: "semantic-version-parsing",
        title: "Semantic version parsing",
        description: "Parse a version string into a record with optional tag and build metadata.",
      },
    ],
  },
  {
    title: "Modelling data",
    description: "Records, tagged unions and protected types standing in for classes and refinement types.",
    examples: [
      {
        file: "constraints.nyx",
        slug: "constraints",
        title: "Constrained values",
        description: "Protected nominal types whose only constructors check an invariant.",
      },
      {
        file: "vendingMachine.nyx",
        slug: "vending-machine",
        title: "Vending machine",
        description: "An immutable state machine whose operations return the next state or a typed error.",
      },
      {
        file: "teamColours.nyx",
        slug: "team-colours",
        title: "Volleyball scoring",
        description: "Custom patterns decide what the latest point means, so scoring is a plain match.",
      },
      {
        file: "wallet.nyx",
        slug: "wallet",
        title: "Bitcoin address classification",
        description: "Classify addresses and derive the script hash an Electrum-style server indexes by.",
      },
      {
        file: "functional-argument-parsing.nyx",
        slug: "argument-parsing",
        title: "Argument parsing",
        description: "Walk a list of command-line arguments into a config record, with proc reporting the first error.",
      },
      {
        file: "traitsAndTypeclasses.nyx",
        slug: "traits-and-typeclasses",
        title: "Traits and typeclasses",
        description: "Structural record types for is-a abstractions, contexts for typeclass-style instances, and instance synthesis.",
      },
      {
        file: "oo-class.nyx",
        slug: "oo-class",
        title: "From a class to a record",
        description: "Translate a caching manager class into a record, attached functions and contexts.",
      },
    ],
  },
  {
    title: "Contexts and effects",
    description: "Contexts as implicit parameters: dependency injection, effects and state.",
    examples: [
      {
        file: "Contexts-as-effects.nyx",
        slug: "contexts-as-effects",
        title: "Contexts as effects",
        description: "A Caesar cipher cracker, then reader and state effects expressed as contexts.",
      },
      {
        file: "Nim.nyx",
        slug: "nim",
        title: "Nim",
        description: "The game of Nim from the effect handlers paper, reworked for Nanyx contexts.",
      },
      {
        file: "sql-utils.nyx",
        slug: "sql-utils",
        title: "SQL utilities",
        description: "Database access through a connection context the caller supplies with use.",
      },
      {
        file: "web-server-shared.nyx",
        slug: "todo-shared",
        title: "Todo app: shared types",
        description: "The types and protocol a todo client and server have in common.",
      },
      {
        file: "todo-web-app.nyx",
        slug: "todo-server",
        title: "Todo app: server",
        description: "A server whose store is a context, so it runs over a database or an in-memory map.",
      },
    ],
  },
  {
    title: "Processing data",
    description: "Tables, trees, images and generated source.",
    examples: [
      {
        file: "csv.nyx",
        slug: "csv",
        title: "CSV from a table literal",
        description: "A table literal of records written out as CSV, then read back and parsed with proc.",
      },
      {
        file: "pairing.nyx",
        slug: "device-pairing",
        title: "Device pairing from XML",
        description: "Extract a pairing payload from an XML tree, with proc collecting the required elements.",
      },
      {
        file: "pixel-sorting.nyx",
        slug: "pixel-sorting",
        title: "Pixel sorting",
        description: "A glitch-art effect that reorders the pixels of each image row by colour.",
      },
      {
        file: "source-generator.nyx",
        slug: "source-generator",
        title: "Source generator",
        description: "Generate C# source with raw strings and interpolation side by side.",
      },
    ],
  },
  {
    title: "Talking to services",
    description: "HTTP clients, JSON and error chaining with proc.",
    examples: [
      {
        file: "EventSubscription.nyx",
        slug: "event-subscription",
        title: "Event ingestion client",
        description: "A small API client with optional fields and a record of functions as an object.",
      },
      {
        file: "malicious-shas.nyx",
        slug: "malicious-shas",
        title: "Malicious hash lookup",
        description: "Check package hashes against an API and write the malicious ones to CSV.",
      },
    ],
  },
  {
    title: "Larger programs",
    description: "Longer examples that bring several features together.",
    examples: [
      {
        file: "editor.nyx",
        slug: "editor-lines",
        title: "Editor line navigation",
        description: "Find the nearest line with content and the next change of indentation.",
      },
      {
        file: "whisper_decoding.nyx",
        slug: "whisper-decoding",
        title: "Speech model decoding loop",
        description: "A greedy decoder as a pure step function and a sampling loop over immutable state.",
      },
      {
        file: "shop.nyx",
        slug: "corner-shop",
        title: "Corner shop",
        description: "Orders checked with proc, receipts, a stock report and an RPN calculator, all runnable today.",
      },
    ],
  },
];

/** Every example in display order, with its page order and group. */
export function allExamples() {
  /** @type {(Example & { order: number; group: ExampleGroup })[]} */
  const result = [];
  let order = 1;
  for (const group of exampleGroups) {
    for (const example of group.examples) {
      result.push({ ...example, order: order++, group });
    }
  }
  return result;
}
