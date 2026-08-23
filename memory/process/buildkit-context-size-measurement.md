---
name: buildkit-context-size-measurement
description: BuildKit's `transferring context: NNN` progress line is not the build context size — it collapses under cache reuse. Measure honestly with a throwaway image + du.
---

Never quote BuildKit's `transferring context: NNN` line as the Docker build context size. It reports incremental/metadata transfer and collapses to a few hundred bytes once the builder already holds the files, so two readings taken under different cache states aren't comparable — the small one looks like a spectacular win when it isn't. Measured on this repo, same tree, same `.dockerignore`: the line reported 532 B on one run and 312 kB on the next, for a context that actually delivered 60.3 MB.

**How to measure it honestly** — build a throwaway image on a fresh builder and ask the image what it got:

```dockerfile
FROM alpine
COPY . /ctx
RUN du -sh /ctx
```

```bash
docker buildx create --name ctxprobe --use
docker buildx build --builder ctxprobe --no-cache --progress=plain \
  -f Dockerfile.ctxprobe --load -t ctxprobe:t .
docker buildx rm ctxprobe
```

To compare two `.dockerignore` variants without touching the committed one, use the per-Dockerfile sidecar: BuildKit prefers `<dockerfile-name>.dockerignore` over `.dockerignore`, so `Dockerfile.ctxprobe.dockerignore` holds the variant. Delete both probe files afterward.

**Why it bit:** a subagent used the progress line as PR evidence for a `.dockerignore` fix and claimed a ~2600x reduction; Peter called the ratio impossible and was right — the real figures showed the fix was worth about 4x, real but far smaller than claimed, and the evidence was fabricated by the measurement method, not the agent.

**Related gotcha:** `.dockerignore` uses Go `filepath.Match` plus `**`, so a bare `bin/` matches only a **top-level** `bin/`, never `src/Foo/bin/`. Nested artifacts need `**/bin/`.
