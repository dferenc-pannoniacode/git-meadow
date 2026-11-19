# Git Meadow

Git Meadow is a local Git workbench for the browser. It runs as a .NET 11 Blazor Interactive Server app and uses the native Git CLI to work with repositories on the host or in a Docker volume.

## Run with Docker

If you previously configured Git or SSH inside a container without a home volume, copy those files out before upgrading. After the upgrade, restore them to `/home/app` with ownership for the `app` user.

```sh
docker compose up --build --detach --wait
```

Open [http://127.0.0.1:5080](http://127.0.0.1:5080). Choose **Explore with a sample repository** to try a real merge graph, two working-tree changes, and the commit workflow. Use **Open repository** for a folder already in the workspace, **Clone repository** for an existing remote, or **New repository** to initialize an empty checkout.

The `workspace` volume keeps repositories across container replacements and image upgrades. The `git-home` volume keeps global Git settings and credentials in `/home/app`. Run `docker compose down` to stop the app and keep both volumes. Adding `--volumes` removes them.

To use an existing host directory for repositories, start the same image with a bind mount at `/workspaces`. The container runs as the image’s `app` user, so that user needs write access to the mounted checkout and its Git metadata.

If you’re upgrading an existing Compose deployment, add `--remove-orphans` to `docker compose up` to free port 5080 from the previous service. This keeps the workspace volume.

The image uses Microsoft’s Alpine ASP.NET Core runtime with globalization and time-zone data. It includes Git and the OpenSSH client for repository operations.

```sh
docker build -t git-meadow .
docker run --rm -p 127.0.0.1:5080:8080 -v git-meadow-home:/home/app \
  --mount "type=bind,source=/absolute/path/to/repositories,target=/workspaces" git-meadow
```

### Configure Git settings and remote authentication

Open **Git settings** in the workspace header. In Docker, the `git-home` volume keeps the files created here across container replacements.

- **Commit identity.** Save a default name and email. Repository-specific identities take precedence.
- **SSH.** Generate an Ed25519 key and add its public key to your Git host. Paste a verified `known_hosts` line for the host, then use its SSH remote URL. The generated private key has no passphrase so Git can use it unattended. An existing passphrase-protected key needs an unlocked SSH agent accessible to Git Meadow.
- **HTTPS.** Enter the HTTPS origin (for example, `https://git.example.com`), a username, and an access token. Use the same origin and username to replace or remove the token. The token is scoped to that origin for Git Meadow and stored in plaintext in `~/.git-meadow-credentials`. Protect access to the volume and its backups.

You can also configure Git and OpenSSH in the container shell with `docker compose exec git-meadow sh` (or `docker exec -it <container> sh` for a standalone container). To manage the files from the host, bind-mount a dedicated directory at `/home/app`. Give the `app` user write access and keep `.ssh` at mode `0700` and private keys at `0600`.

### Pull the published image

The [Docker publish workflow](.github/workflows/publish-docker.yml) builds an image when you push a new version tag for a commit on `main`. Push `main` first, then create and push the release tag.

```sh
git push origin main
git tag -a v1.2.3 -m "Release 1.2.3" main
git push origin tag v1.2.3
```

Use `v1.2.3` for a stable release or `v1.2.3-rc.1` for a prerelease. The Git tag becomes the image tag at `ghcr.io/owner/repository`, and Git Meadow shows the version in the sidebar. Stable releases also update `latest`. An untagged local build shows `0.0.0-local`.

After the first successful run, open the new package on GitHub and choose **Package settings → Danger Zone → Change visibility → Public**. GitHub creates the package as private, even for a public repository. Once the package is public, anyone can pull it without signing in. See [GitHub's package visibility instructions](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility#configuring-visibility-of-packages-for-your-personal-account).

Replace `owner/repository` with the GitHub repository path in lowercase:

```sh
image=ghcr.io/owner/repository:v1.2.3
docker pull "$image"
docker run --rm -p 127.0.0.1:5080:8080 -v git-meadow-workspace:/workspaces \
  -v git-meadow-home:/home/app --cap-drop ALL --security-opt no-new-privileges:true "$image"
```

## Run with .NET

Install the .NET SDK version specified in `global.json` and Git 2.40 or later on the machine that owns the repository files.

```powershell
$env:GitMeadow__Workspace = 'C:\Projects'
dotnet run --project src/GitMeadow
```

Open [http://127.0.0.1:5080](http://127.0.0.1:5080). The workspace can contain multiple checkouts or be a checkout itself. The sidebar discovers the root and its direct children. To open a deeper checkout, enter its workspace-relative path. Without an override, the host uses `workspace/` in the application’s content root.

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

Git Meadow runs Git as a separate executable. The image includes Git Meadow’s license and third-party notices in `/usr/share/doc/git-meadow/` and Git’s license in `/usr/share/doc/git-meadow/git-source/COPYING`.

The image ships the Git Meadow source used to build it, including the Dockerfile and build configuration, in `/usr/share/doc/git-meadow/app-source/`. The exact source for the installed Alpine Git package, including the upstream archive, Alpine patches and build recipe, ships in `/usr/share/doc/git-meadow/git-source/`. Its `package.txt` records the package version and Alpine packaging commit.

To extract both from a locally built image, run the commands below. For a prebuilt image, replace the `git-meadow` image name with the image tag you pulled.

```sh
docker create --name git-meadow-source git-meadow
docker cp git-meadow-source:/usr/share/doc/git-meadow ./git-meadow-source
docker rm git-meadow-source
```

When distributing a prebuilt image, keep these files with the image and give recipients the extraction instructions alongside the download or pull instructions. Check the redistribution terms for the other bundled packages, including those inherited from the base image. [Git license](https://git-scm.com/about) · [GPLv2, Sections 2–3](https://www.gnu.org/licenses/old-licenses/gpl-2.0.en.html)

## Verify

```sh
dotnet restore GitMeadow.slnx --locked-mode
dotnet test GitMeadow.slnx --no-restore
```

After an intentional dependency update, regenerate the NuGet lock files with `dotnet restore GitMeadow.slnx --force-evaluate`. For SDK upgrades, update `global.json` and the Docker build-stage SDK tag together before regenerating the locks. The SDK selects the Blazor assets package recorded in the app lock file.

The integration suite creates disposable Git repositories and exercises actual index, ref, reachability, and working-tree behavior. It needs Git on `PATH` and requires no Git hosting credentials. Docker health checks probe both the web server and the Blazor browser runtime.
