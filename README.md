# Git Meadow

Git Meadow is a local Git workbench for the browser. It runs as a .NET 11 Blazor Interactive Server app and uses the native Git CLI to work with repositories on the host or in a Docker volume.

## Run with Docker

### Use the published image

Save this as `compose.yaml` in a deployment directory. The `v0.9.0-rc14` prerelease supports `linux/amd64` and `linux/arm64`. Docker selects the matching image for your host. You can choose a different tag from the [Git Meadow container package](https://github.com/dferenc-pannoniacode/git-meadow/pkgs/container/git-meadow).

```yaml
services:
  git-meadow:
    image: ghcr.io/dferenc-pannoniacode/git-meadow:v0.9.0-rc14
    ports:
      - "127.0.0.1:5081:8080"
    environment:
      GitMeadow__AntiforgeryCookieName: .AspNetCore.Antiforgery.GitMeadow-${COMPOSE_PROJECT_NAME}
    volumes:
      - workspace:/workspaces
      - git-home:/home/app
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
volumes:
  workspace: {}
  git-home: {}
```

Run this from the directory containing `compose.yaml`.

```sh
docker compose up --pull always --detach --wait
```

To upgrade, change the image tag and rerun the command with the same Compose project name so Compose reuses the existing volumes.

### Build from source

From the repository root, use the checked-in [compose.yaml](compose.yaml).

```sh
docker compose up --build --detach --wait
```

### Open Git Meadow and manage storage

Open [http://127.0.0.1:5081](http://127.0.0.1:5081). Choose **Explore with a sample repository** to try the history and commit workflows, or use **Open repository**, **Clone repository**, or **New repository** to work with your own checkout.

The `workspace` volume stores repositories. The `git-home` volume keeps global Git settings, credentials, and ASP.NET Core Data Protection keys in `/home/app`. `docker compose down` stops the app and keeps both volumes. Adding `--volumes` deletes them.

To use a host directory, replace `workspace:/workspaces` with `"/absolute/path/to/repositories:/workspaces"`. The container’s `app` user needs write access to the mounted checkouts and their Git metadata.

Compose sets `GitMeadow__AntiforgeryCookieName` from the project name. Builds that support this setting use a distinct cookie name for each Compose project on the same hostname.

### Configure Git settings and remote authentication

Open **Git settings** in the workspace header. In Docker, the `git-home` volume keeps the files created here across container replacements.

- **Commit identity.** Save a default name and email. Repository-specific identities take precedence.
- **SSH.** Generate an Ed25519 key and add its public key to your Git host. Paste a verified `known_hosts` line for the host, then use its SSH remote URL. The generated private key has no passphrase so Git can use it unattended. An existing passphrase-protected key needs an unlocked SSH agent accessible to Git Meadow.
- **HTTPS.** Enter the HTTPS origin (for example, `https://git.example.com`), a username, and an access token. Use the same origin and username to replace or remove the token. The token is scoped to that origin for Git Meadow and stored in plaintext in `~/.git-meadow-credentials`. Protect access to the volume and its backups.

You can also configure Git and OpenSSH in the container shell with `docker compose exec git-meadow sh` (or `docker exec -it <container> sh` for a standalone container). To manage the files from the host, bind-mount a dedicated directory at `/home/app`. Give the `app` user write access and keep `.ssh` at mode `0700` and private keys at `0600`.

### Use Git LFS

The Docker image includes Git LFS with its file filters enabled. Open **Repository → Git LFS → Track files with LFS** and enter a pattern such as `*.zip`. Stage `.gitattributes` along with the matching files, then commit and use **Push**. Git Meadow uploads the LFS objects as part of the push.

Git downloads LFS contents when a pull checks out files with the LFS filters enabled. Use **Repository → Git LFS → Download LFS files** to fetch contents for the current checkout when it already contains pointers, even if the branch is up to date. Git LFS uses the repository’s configured LFS endpoint when one is set.

## Run with .NET

Install the .NET SDK version specified in `global.json` and Git 2.40 or later on the machine that owns the repository files. Install Git LFS 3.7.1 or later to use the Git LFS controls.

```powershell
$env:GitMeadow__Workspace = 'C:\Projects'
dotnet run --project src/GitMeadow
```

Open [http://127.0.0.1:5080](http://127.0.0.1:5080). The workspace can contain multiple checkouts or be a checkout itself. The sidebar discovers the root and its direct children. Choose **Open repository** to browse for deeper checkouts inside the workspace. Without an override, the host uses `workspace/` in the application’s content root.

## Workflows

| Area | Available in Git Meadow |
| --- | --- |
| History | Commit graph with parent edges, reference filtering, commit search, older history loading, author and message details, revision comparison, unified diffs, notes, patch download |
| Files | Browse paths at a revision, view text files, follow file history, inspect blame |
| Working tree | Staged, unstaged, untracked and conflicted files, staging and unstaging by file, hunk, or selected text lines, discard, commit, amend, side selection and text conflict editing |
| References | Local and remote branches, detached checkout, lightweight and annotated tags, branch/tag deletion, upstream tracking |
| Integrate and rewrite | Merge, rebase and rebase onto, editable interactive rebase plan, squash, cherry-pick, revert, soft/mixed/hard reset, continue/skip/abort |
| Remotes | Clone, add/edit/remove remotes, fetch, fast-forward pull, push branches and tags |
| Repository tools | Stashes with untracked files, reflog, worktrees, submodule creation and inspection, recursive network submodule updates, bisect, patch application, commit identity |

Right-click the base commit in History and choose **Interactive rebase from here**. The rows list commits oldest first. Move the rows to reorder them, or change a row’s action. A reword or edit stop opens the Changes view. Use **Amend last commit** and then **Continue**.

In **Changes**, select a staged or unstaged file and expand a diff hunk. Check the additions and deletions you want, then choose **Stage selected lines** or **Unstage selected lines**. For a replacement, select both the removed line and the added line to move the whole edit. Use the whole-file controls for binary diffs or diffs containing a Unicode replacement character.

Git runs as the host or container user. This deployment is intended for one local user and trusted checkouts. Git can read existing repository configuration, so use a dedicated workspace or volume. Remote authentication uses credentials saved in **Git settings** or credentials already configured for the running Git process. The app runs Git without interactive credential prompts. Supported network transports are HTTPS, HTTP, and SSH. Local remotes must be inside the workspace. For local-path submodules, use an explicit add from Git Meadow or run subsequent updates in local Git. Avoid concurrent changes to the same checkout from an external Git process.

### Git Extensions coverage

Git Meadow follows the main repository workflows in the Git Extensions 7.2 manual. Coverage is partial. Remaining gaps include GPG verification and signing, advanced merge options, provider pull-request and build-report integration, and the Windows Explorer, Visual Studio, and plugin integrations. The workbench loads up to 5,000 history rows per ref selection. Git diff and file views are size-limited. [Git Extensions 7.2 manual](https://git-extensions-documentation.readthedocs.io/en/release-7.2/)

## License and commercial support

Git Meadow is free and open source for personal and commercial use under the [GNU Affero General Public License, version 3](LICENSE) (`AGPL-3.0-only`). Businesses can purchase optional support under a separate agreement. Contact the project maintainers to discuss onboarding, troubleshooting, response times, and pricing.

If you distribute a compiled Git Meadow build, make its corresponding source available to recipients under the AGPLv3. If you run a modified version for people who use it remotely over a network, offer those users the corresponding source at no charge. See Sections 5, 6, and 13 of the license.

The Blazor template files retain their [upstream MIT notice](THIRD-PARTY-NOTICES).

### Source in the Docker image

Git Meadow runs Git as a separate executable. The image includes Git Meadow’s license and third-party notices in `/usr/share/doc/git-meadow/`, Git’s license in `/usr/share/doc/git-meadow/git-source/COPYING`, and Git LFS’s license in `/usr/share/doc/git-meadow/git-lfs/LICENSE.md`.

The image ships the Git Meadow source used to build it, including the Dockerfile and build configuration, in `/usr/share/doc/git-meadow/app-source/`. The exact source for the installed Alpine Git package, including the upstream archive, Alpine patches and build recipe, ships in `/usr/share/doc/git-meadow/git-source/`. Its `package.txt` records the package version and Alpine packaging commit.

Once the Compose container exists, copy the bundled source from the deployment directory.

```sh
docker compose cp git-meadow:/usr/share/doc/git-meadow ./git-meadow-source
```

When distributing a prebuilt image, keep these files with the image and give recipients the extraction instructions alongside the download or pull instructions. Check the redistribution terms for the other bundled packages, including those inherited from the base image. [Git license](https://git-scm.com/about) · [GPLv2, Sections 2–3](https://www.gnu.org/licenses/old-licenses/gpl-2.0.en.html)

## Verify

```sh
dotnet restore GitMeadow.slnx --locked-mode
dotnet test GitMeadow.slnx --no-restore
```

After an intentional dependency update, regenerate the NuGet lock files with `dotnet restore GitMeadow.slnx --force-evaluate`. For SDK upgrades, update `global.json` and the Docker build-stage SDK tag together before regenerating the locks. The SDK selects the Blazor assets package recorded in the app lock file.

The integration suite creates disposable Git repositories and exercises actual index, ref, reachability, and working-tree behavior. It needs Git on `PATH` and requires no Git hosting credentials. Docker health checks probe both the web server and the Blazor browser runtime.
