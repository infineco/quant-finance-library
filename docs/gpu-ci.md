# GPU continuous integration

The regular CI (`.github/workflows/ci.yml`) runs on GitHub-hosted runners, which have no GPU: the CUDA tests are skipped
there. The `GPU` workflow (`.github/workflows/gpu.yml`) closes that gap. It runs nightly on a self-hosted runner with an
NVIDIA GPU, generates the CUDA satellite with Hybridizer, builds the solution and runs the whole test suite, so the
GPU path is shown to keep working. The environment is the Docker image described in `build/hybridizer/linux/Dockerfile`
(CUDA 13.0, .NET 8, Hybridizer 7.6.3), also published to GHCR by `.github/workflows/docker-image.yml`.

## Register the self-hosted runner

On the GPU host (Linux x64, NVIDIA driver installed):

1. Install Docker and the NVIDIA Container Toolkit, then check that containers see the GPU:

   ```sh
   docker run --rm --gpus all ubuntu:24.04 nvidia-smi -L
   ```

   It must list your GPU. If not, fix the toolkit first (`nvidia-ctk runtime configure --runtime=docker`, restart Docker).
2. The workflow uses `runs-on: ubuntu-gpu`, the label of the runners the Hybridizer repositories already use
   (see `runners/` next to this repository: ephemeral runners registered with `config.sh --ephemeral`). Those runners
   must be registered for this repository or its organization (`infineco`) and carry the `ubuntu-gpu` label, and the
   runner group must allow this repository. For a runner dedicated to this repository instead, go to Settings > Actions
   > Runners > New self-hosted runner (Linux, x64) and pass the label:

   ```sh
   ./config.sh --url https://github.com/<owner>/<repo> --token <token> --labels ubuntu-gpu
   ```
3. Run it as a service (`sudo ./svc.sh install && sudo ./svc.sh start`). The runner user must be in the `docker` group.

Use a dedicated machine or at least a dedicated user. The workflow starts containers as root against the checked-out
workspace, and hands the files back to the runner user at the end of each run.

## Security notes (public repository + self-hosted runner)

A self-hosted runner executes workflow code directly on your machine, with access to its network and disk. On a public
repository, anyone can open a pull request, so GitHub itself recommends against self-hosted runners for public repos
unless the triggers are strictly controlled. This workflow is:

- `schedule` and `workflow_dispatch`: only code from the default branch, started by the clock or by a user with write access;
- `pull_request`: runs only when the PR branch is in this same repository (never a fork) and carries the `gpu` label.
  Only users with write access can apply labels, so a maintainer reviews the change first; any push after the label is
  applied reruns the job, so only review and label code from trusted contributors.

Additional hardening to apply on the repository: in Settings > Actions > General, set "Fork pull request workflows
from outside collaborators" to "Require approval for all outside collaborators". Prefer a runner group restricted to the
repositories you trust, give the runner no secrets, and do not add `pull_request_target` triggers to `gpu.yml`.

## Trigger a run

- Nightly: automatic at 02:30 UTC on the default branch.
- Manually: Actions > GPU > Run workflow, or `gh workflow run gpu.yml`.
- On a pull request from this repository: add the `gpu` label (create it once under Issues > Labels).

Runs are serialised by a concurrency group, so two GPU runs never overlap.

## Where results are

- The workflow log starts with `nvidia-smi`, the Docker and CUDA versions.
- Artifact `gpu-test-results` holds the `.trx` files of every test project (kept even when tests fail);
  artifact `cuda-satellite` holds the generated satellite for 7 days.
- The job fails if the CUDA build fails or any test fails. Failed nightly runs notify the repository maintainers by
  the usual GitHub Actions notifications.

## Published image and one-command local demo

On every change to `build/hybridizer/linux/**` on `main`, and on `v*` tags, the image is pushed as
`ghcr.io/<owner>/quant-finance-cuda` with the tags `latest`, `sha-<commit>` and the version for release tags.

With a GPU and the NVIDIA Container Toolkit, from a checkout of the repository:

```sh
docker run --rm --gpus all -v "$PWD":/work -w /work ghcr.io/<owner>/quant-finance-cuda:latest bash -lc \
  "dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true \
   && dotnet build QuantFinance.sln -c Release && dotnet test QuantFinance.sln -c Release --no-build"
```

The device code is JIT-compiled by nvrtc for the GPU visible at build time, so build and run on the same GPU model.
See `docs/gpu.md` for the details and known issues.
