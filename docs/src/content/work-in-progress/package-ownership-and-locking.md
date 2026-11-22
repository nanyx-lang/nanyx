---
title: "Package ownership and locking"
description: "Keeping a namespace through a GitHub rename, publishing under an organisation, and what the lockfile has still to record"
order: 6
---

A namespace belongs to the GitHub account of the same name, and how the index checks it, what `nanyx.lock` holds and
why, are described on [The package index](../deep-dives/package-index). Dependencies on GitHub and scripts'
dependencies are described in [Packages and projects](../tour/packages#dependencies-on-github) and
[Files and projects](../getting-started/files-and-projects#directives). This page proposes what is left: keeping a
namespace's owner by account ID, publishing under an organisation, and the lockfile's remaining parts. It is part of
the [packaging](./packaging) workstream, and the [Open questions](#open-questions) are at the end.

# The claim is kept by account ID

A GitHub account can be renamed, and once it is, its old name is free for anyone to register. If ownership followed the name alone, the next person to register `acme` would own every `acme` package. Elm, whose package names are GitHub names, has this problem. The index's check compares a namespace with the repository owner's current login, so it has this problem too.

So the name is to decide only the **first** claim. On the first publish to a namespace, the index records the owner's numeric GitHub ID, which never changes, and every later publish is checked against the ID:

```toml
# namespaces/acme.toml, in the package index repository
owner = { kind = "organisation", id = 9919, claimed-as = "acme" }
publishers = [583231, 1024025]
```

Nobody types an ID. The publish workflow reads the author's ID from the pull request that `nanyx publish` opens, and writes this file itself. Installing a package never involves an ID either: a dependant writes `(package = "acme.web", version = "1.2")` and the client reads the index, never GitHub.

| What happens on GitHub | What happens to the namespace |
| --- | --- |
| `acme` renames itself `acme-corp` | `acme` keeps publishing under `acme`, because its ID still matches. Dependants change nothing. |
| Someone else registers the freed name `acme` | Their publishes are refused, because their ID does not match. |
| `acme` deletes its account | The namespace is frozen. Published versions keep installing, and nothing new can be published. Giving it to someone else is a decision for the Nanyx maintainers, made by hand. |

A namespace does not follow its owner's renames. `acme.web` stays `acme.web` however its owner is named on GitHub, because renaming a package would break every program that imports it.

# Publishing under an organisation

A user's namespace has one publisher: that user. An organisation's namespace has the list of `publishers` in its namespace file. Changing the list is a pull request to the index repository, accepted automatically when its author is already a publisher.

The first claim is harder. GitHub only shows that a user belongs to an organisation if they have made their membership public, so the workflow cannot always see that a pull request's author belongs to `acme`. The proposal is that the first claim to an organisation's namespace comes from a **public member** of the organisation, who becomes its first publisher. When publishing later moves from pull requests to a service, a release built by GitHub Actions in a repository the organisation owns can prove the organisation directly: the job's OIDC token names the repository's owner.

# The lockfile

What is still planned for `nanyx.lock`:

- **One lockfile per build.** A package on its own has `nanyx.lock` beside its `.nyxp`. In a [project](../tour/packages#projects) the lockfile is to be beside the `.nyxproj` and cover every package in it, because a build has one version of each package and packages built together must agree. [Project references](../tour/packages#packages-developed-together) are not locked, as path dependencies aren't, but their own dependencies named by version are locked with the build's.
- **`nanyx update`** upgrades a locked version on purpose, for one package or all of them ([#2265](https://github.com/nanyx-lang/nanyx/issues/2265)).
- **GitHub references.** Every locked package comes from the index, so an entry has no `source`. One is to be added when a GitHub reference with a tag in its `rev` is locked by its commit, and the lockfile is to record the SHA-256 of what was downloaded for a GitHub reference too.

# Open questions

- **Hyphens in names.** GitHub names can contain `-`. Can a package name segment, so that `acme-corp.web/http` is a valid import? If not, how does an account with `-` in its name get a namespace?
- **Organisations that never claim their namespace.** A popular user could publish under their own namespace something an organisation would want to own, or an organisation's namespace could stay unclaimed. Is "first claim by the account of the same name" enough, or should well-known organisations be reserved?
- **Frozen namespaces.** When an owner's GitHub account is deleted, what does it take for the maintainers to hand the namespace to someone else, and are its old packages kept under the old owner?
- **Other identity providers.** GitLab or Codeberg accounts, or a domain proved by DNS as Maven Central does. Each would need a prefix, such as `gitlab-acme`, or a rule about which provider a bare name belongs to.
- **Copying files into published packages.** Should `nanyx publish` copy a file from GitHub into the package rather than refusing it, and if so, how is the licence handled?
- **Are file dependencies worth having?** Copying a file into a program already works. What a `module` entry adds is a record of where the file came from and an easy update. Is that enough to justify a second kind of dependency, its lockfile entries and its rules?
- **Files from other hosts.** Should `github =` have siblings, such as `gist =` or `gitlab =`, or is `url =` enough? Resolving a tag or branch to a commit needs the host's git protocol, which a plain `url` does not have, so `url` files would be pinned by hash only.
- **A package in a subdirectory of a repository.** A `path` within the repository, for a GitHub dependency whose `.nyxp` isn't at its root.
