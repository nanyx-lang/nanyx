---
title: "Package ownership and locking"
description: "Who can publish under a package name, what the lockfile records, and depending on a single file"
order: 6
---

> **Status: proposal.** None of this is implemented. It fills in three parts of the [packaging design](https://github.com/nanyx-lang/nanyx/blob/main/compiler/design/packaging.md) that are only sketched there: who owns a package name, what `nanyx.lock` holds, and whether a program can depend on one file rather than a package. The packaging work is tracked in [#207](https://github.com/nanyx-lang/nanyx/issues/207), and the package index in [#209](https://github.com/nanyx-lang/nanyx/issues/209). See [Open questions](#open-questions) at the end.

[Packages and projects](../tour/packages) describes what a user writes: a `.nyxpack` manifest, `[dependencies]` such as `acme.core = "2.1"`, and imports such as `acme.web/http`. This page is about what sits behind those names.

# Who owns a package name

A package name is dotted, and its first segment is its **namespace**: `acme` in `acme.web`. Every published package has at least two segments, so nobody can claim a bare name such as `json` by getting there first. A namespace has one owner, and only its owner can publish under it.

## Owners are GitHub accounts

Nanyx does not run an account system. At launch, a namespace belongs to the GitHub user or organisation of the same name: `acme.web` can be published by the GitHub account `acme`, and by nobody else. GitHub proves who someone is, and the name decides which namespace is theirs, so there is nothing to apply for and no one deciding who gets a name.

`nanyx` is the exception. It is reserved for the Nanyx project, whose GitHub organisation is `nanyx-lang`, and holds the standard library and official packages such as `nanyx.http`.

## The claim is kept by account ID

A GitHub account can be renamed, and once it is, its old name is free for anyone to register. If ownership followed the name alone, the next person to register `acme` would own every `acme` package. Elm, whose package names are GitHub names, has this problem.

So the name only decides the **first** claim. On the first publish to a namespace, the index records the owner's numeric GitHub ID, which never changes, and every later publish is checked against the ID:

```toml
# namespaces/acme.toml, in the package index repository
owner = { kind = "organisation", id = 9919, claimed-as = "acme" }
publishers = [583231, 1024025]
```

Nobody types an ID. The publish workflow reads the author's ID from the pull request that `nanyx publish` opens, and writes this file itself. Installing a package never involves an ID either: a dependant writes `acme.web = "1.2"` and the client reads the index, never GitHub.

| What happens on GitHub | What happens to the namespace |
| --- | --- |
| `acme` renames itself `acme-corp` | `acme` keeps publishing under `acme`, because its ID still matches. Dependants change nothing. |
| Someone else registers the freed name `acme` | Their publishes are refused, because their ID does not match. |
| `acme` deletes its account | The namespace is frozen. Published versions keep installing, and nothing new can be published. Giving it to someone else is a decision for the Nanyx maintainers, made by hand. |

A namespace does not follow its owner's renames. `acme.web` stays `acme.web` however its owner is named on GitHub, because renaming a package would break every program that imports it.

## Publishing under an organisation

A user's namespace has one publisher: that user. An organisation's namespace has the list of `publishers` in its namespace file. Changing the list is a pull request to the index repository, accepted automatically when its author is already a publisher.

The first claim is harder. GitHub only shows that a user belongs to an organisation if they have made their membership public, so the workflow cannot always see that a pull request's author belongs to `acme`. The proposal is that the first claim to an organisation's namespace comes from a **public member** of the organisation, who becomes its first publisher. When publishing later moves from pull requests to a service, a release built by GitHub Actions in a repository the organisation owns can prove the organisation directly: the job's OIDC token names the repository's owner.

## How names are spelled

Names are lower case, and two names that differ only in case, in `-` and `_`, or in characters that look alike are the same name, to make typosquatting harder. A GitHub name is mapped to its namespace by lowering its case. GitHub names can contain `-`, so a package name segment can too, which [is open](#open-questions) while import syntax has no rule for it.

## Why not something else

- **Names chosen on the registry**, as on JSR and npm, where a scope is a name you claim. Owners could pick a better name than their GitHub handle, but someone has to allocate names and settle disputes, and Nanyx would need its own accounts or a rule about who may claim what.
- **Flat names**, as on crates.io and PyPI. The good names go to whoever comes first. Every registry that started flat has tried to add namespaces later.
- **The repository URL as the name**, as in Go. There is no index to run, but a package's name is where its code lives, so moving the code renames the package.

# The lockfile

`nanyx.lock` records every package a build uses: its exact version, where it came from and the SHA-256 of what was downloaded. With a lockfile and the packages in the cache, a build needs nothing from the network, and two machines with the same lockfile build the same program.

```toml
# Written by nanyx. Do not edit by hand.
version = 1

[[package]]
name = "acme.core"
version = "2.3.1"
source = "index"
sha256 = "9f2c41…"

[[package]]
name = "acme.web"
version = "1.2.0"
source = "index"
sha256 = "e07a5d…"
dependencies = ["acme.core"]
```

- **One lockfile per build.** A package on its own has `nanyx.lock` beside its `.nyxpack`. In a [project](../tour/packages#projects) the lockfile is beside the `.nyxproj` and covers every package in it, because a build has [one version of each package](https://github.com/nanyx-lang/nanyx/blob/main/compiler/design/packaging.md#versions-and-resolution) and packages built together must agree.
- **It is committed**, for libraries as well as programs, so that a library's own tests and CI build the same thing every time. A dependant ignores a library's lockfile and resolves for itself.
- **It changes only when it must.** `nanyx add` and `nanyx restore` write it. `nanyx build` and `nanyx run` use it as it is, and only update it when the manifest asks for something the lockfile does not satisfy, such as a new dependency. A version already locked is never upgraded as a side effect: `nanyx update` does that, for one package or all of them.
- **`--locked` makes a stale lockfile an error.** CI runs `nanyx build --locked`, so a manifest change pushed without its lockfile fails instead of resolving something new.
- **Entries are sorted by name**, and each sits in its own table, so two branches adding different dependencies rarely conflict, and when they do, `nanyx restore` can redo the resolution from the manifests.
- **Path dependencies and [project references](../tour/packages#project-references) are not locked.** Their source is on disk and changes with the code around it, so there is nothing to pin.

`source` says where a package came from. `"index"` means the package index. The other sources are a git repository and a single file, described below.

## Scripts

A script has no manifest, and `#:package` directives stand in for `[dependencies]`. Where its lockfile goes is [open](#open-questions). The proposal is a `hello.nyx.lock` beside `hello.nyx`, written the first time the script runs with a dependency. A script without one still runs and resolves its dependencies each time, which is what someone running a one-off script expects.

# Depending on a single file

Some code is too small to be worth a package: a retry helper, a slug function, a few lines copied from a gist. Today the choices are to publish it or to copy it into the program. Copying loses where it came from and makes updates manual. [Paket](https://fsprojects.github.io/Paket/github-dependencies.html), the F# package manager, lets a project depend on a single source file from GitHub, pinned to a commit.

Nanyx suits this better than most languages. Packages are distributed as source and a program compiles to one WebAssembly module, so a file from elsewhere is compiled the same way as a file in the program, with no separate compiled form.

## Declaring a file

A package lists the files it depends on in a `[files]` table:

```toml
[files]
retry = { github = "acme/snippets", path = "retry.nyx", rev = "v1.0" }
slugify = { url = "https://example.com/nanyx/slugify.nyx" }
```

The key is the module name the file gets in this package. A package's files must all contain named modules, and a downloaded file has no name of its own, so the manifest gives it one:

```nanyx
import retry

def fetchAll = { urls ->
  urls \map({ url -> retry.withBackoff(3, { fetch(url) }) })
}
```

In a script, a `#:file` directive does the same job:

```nanyx
#:file retry acme/snippets/retry.nyx@v1.0

import retry
```

`rev` is a tag, a branch or a commit. Whichever it is, the lockfile records the commit it pointed at and the file's SHA-256, so a moving branch does not change a locked build:

```toml
[[file]]
name = "retry"
source = "github:acme/snippets/retry.nyx"
rev = "v1.0"
commit = "1f8ae8b3c9…"
sha256 = "b4d0e2…"
```

`nanyx update retry` moves it to wherever `rev` now points.

## What a file can use

A file has no manifest, so it cannot declare dependencies. It can import the standard library and nothing else: no packages and no other files by path. A file that needs more than that is a package.

A file is held to the same rules as any other code. Anything it does through a context, such as reading files or using the network, shows in the types of its definitions, and the caller provides the context. A downloaded file is not trusted more than a package, but it is not trusted less either.

It is a module of the package that depends on it, but it cannot be listed in `public-modules`: a package's public API is code the package publishes, and a file is not published with it.

## Files and publishing

`nanyx publish` refuses a package with a `[files]` table. A published package has to keep building for as long as anyone depends on it, and a file on someone else's GitHub repository can disappear. The same rule applies to git dependencies, below.

Copying the file into the published package instead is [open](#open-questions). It would keep the build working, but the file's licence travels with it, and the publish step cannot check that it allows redistribution.

# Dependencies from git

A package-sized version of the same idea is a dependency on a git repository, as Cargo, Mix and others allow:

```toml
[dependencies]
acme.web = { git = "https://github.com/acme/web", tag = "v1.2.0" }
```

The repository must contain a `.nyxpack` whose `name` is the dependency's key. The lockfile records the commit, and `nanyx publish` refuses a package that has one. This is for trying a fork or a fix that has not been released, not a second way to distribute packages: names on the index stay the only names a published package can depend on.

Because a git dependency never touches the index, the ownership rules above do not apply to it. Choosing to depend on someone's repository is the dependant's decision.

# Open questions

- **Hyphens in names.** GitHub names can contain `-`. Can a package name segment, so that `acme-corp.web/http` is a valid import? If not, how does an account with `-` in its name get a namespace?
- **Organisations that never claim their namespace.** A popular user could publish under their own namespace something an organisation would want to own, or an organisation's namespace could stay unclaimed. Is "first claim by the account of the same name" enough, or should well-known organisations be reserved?
- **Frozen namespaces.** When an owner's GitHub account is deleted, what does it take for the maintainers to hand the namespace to someone else, and are its old packages kept under the old owner?
- **Other identity providers.** GitLab or Codeberg accounts, or a domain proved by DNS as Maven Central does. Each would need a prefix, such as `gitlab-acme`, or a rule about which provider a bare name belongs to.
- **Script lockfiles.** A file beside the script, the cache, or a lock written into the script's own `#:` directives so the script stays one file?
- **Copying files into published packages.** Should `nanyx publish` copy a `[files]` dependency into the package rather than refusing it, and if so, how is the licence handled?
- **Is `[files]` worth having?** Copying a file into a program already works. What `[files]` adds is a record of where the file came from and an easy update. Is that enough to justify a second kind of dependency, its lockfile entries and its rules?
- **Files from other hosts.** Should `github =` have siblings, such as `gist =` or `gitlab =`, or is `url =` enough? Resolving a tag or branch to a commit needs the host's git protocol, which a plain `url` does not have, so `url` files are pinned by hash only.
