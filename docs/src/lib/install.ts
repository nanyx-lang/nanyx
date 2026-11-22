// The short install instructions on the home page, by operating system. Their commands are read from the
// Installation page's code blocks, so that the two can't say different things: when that page's sections change,
// `installSteps` throws and the build fails, rather than the home page going on showing the old commands.
import installation from "@/content/getting-started/installation.md?raw";

export type InstallOs = "windows" | "macos" | "linux";

export type InstallStep = {
  /** What the step does, as HTML: it may hold a link or inline code. */
  text: string;
  /** The commands, if the step has any, and their language for highlighting. */
  code?: { lang: string; code: string };
};

type CodeBlock = { lang: string; code: string };

/** The fenced code blocks under the heading `heading` of the Installation page, up to the next heading of its level or higher. */
function blocksUnder(heading: string): CodeBlock[] {
  const lines = installation.replace(/\r\n/g, "\n").split("\n");
  const start = lines.findIndex((line) => /^#+\s/.test(line) && line.replace(/^#+\s+/, "") === heading);
  if (start < 0) throw new Error(`The Installation page has no heading "${heading}", which the home page reads.`);
  const level = /^#+/.exec(lines[start])![0].length;
  const blocks: CodeBlock[] = [];
  for (let index = start + 1; index < lines.length; index++) {
    const headingMatch = /^(#+)\s/.exec(lines[index]);
    if (headingMatch && headingMatch[1].length <= level) break;
    const fence = /^```(\S*)/.exec(lines[index]);
    if (!fence) continue;
    let close = index + 1;
    while (close < lines.length && !lines[close].startsWith("```")) close++;
    blocks.push({ lang: fence[1] || "text", code: lines.slice(index + 1, close).join("\n") });
    index = close;
  }
  return blocks;
}

/** The blocks under `heading`, which must be `count` of them. */
function exactly(heading: string, count: number): CodeBlock[] {
  const blocks = blocksUnder(heading);
  if (blocks.length !== count) {
    throw new Error(
      `The Installation page has ${blocks.length} code blocks under "${heading}"; the home page expects ${count}. Update src/lib/install.ts.`,
    );
  }
  return blocks;
}

const releases = "https://github.com/nanyx-lang/nanyx/releases";

/** Each operating system's steps, in the order the home page shows them. */
export function installSteps(): Record<InstallOs, InstallStep[]> {
  const [scoop] = exactly("Windows (Scoop)", 1);
  const [extract, path, quarantine] = exactly("macOS and Linux", 3);
  const [verify] = exactly("Verify installation", 1);
  const check: InstallStep = { text: "Check it, in a new terminal:", code: verify };
  /** The download step, for `files`, HTML naming the archives. */
  const download = (files: string): InstallStep => ({
    text: `Download ${files} from the newest <a href="${releases}">Compiler release</a>.`,
  });
  return {
    windows: [{ text: 'Install with <a href="https://scoop.sh">Scoop</a>:', code: scoop }, check],
    macos: [
      download("<code>nanyx-&lt;version&gt;-osx-arm64.tar.gz</code> (<code>osx-x64</code> on Intel)"),
      { text: "Extract it into a folder of its own:", code: extract },
      { text: "Put the folder on your <code>PATH</code>, in <code>~/.zshrc</code>:", code: path },
      { text: "The release isn't signed, so let macOS run it:", code: quarantine },
      check,
    ],
    linux: [
      download("<code>nanyx-&lt;version&gt;-linux-x64.tar.gz</code> (<code>linux-arm64</code> on ARM)"),
      { text: "Extract it into a folder of its own:", code: extract },
      { text: "Put the folder on your <code>PATH</code>, in <code>~/.bashrc</code>:", code: path },
      check,
    ],
  };
}
