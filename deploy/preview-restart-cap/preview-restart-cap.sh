#!/bin/bash
# Caps restarts of Coolify PR preview containers on the NUC. Coolify hardcodes `restart: unless-stopped`
# (RESTART_MODE in its constants.php) and its custom Docker options cannot override it, so a preview whose
# startup fails would restart forever. This switches each preview container to `on-failure:3` as soon as
# Docker creates it; base deploys (coolify.pullRequestId=0) keep Coolify's policy.
set -u

cap() {
  docker update --restart=on-failure:3 "$1" > /dev/null && echo "capped restarts of $2 (pr-$3)"
}

# Containers created while this service was down.
docker ps -a --filter label=coolify.pullRequestId \
  --format '{{.ID}} {{.Names}} {{.Label "coolify.pullRequestId"}}' |
while read -r id name pr; do
  [ "$pr" != "0" ] && cap "$id" "$name" "$pr"
done

docker events --filter type=container --filter event=create --filter label=coolify.pullRequestId \
  --format '{{.Actor.ID}} {{.Actor.Attributes.name}} {{index .Actor.Attributes "coolify.pullRequestId"}}' |
while read -r id name pr; do
  [ "$pr" != "0" ] && cap "$id" "$name" "$pr"
done
