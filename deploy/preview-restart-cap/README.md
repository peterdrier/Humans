# Preview restart cap (NUC host)

Stops a broken PR preview from restarting forever: each preview container gets `on-failure:3`
instead of Coolify's fixed `unless-stopped`, so after three failed restarts it stays `Exited`.

Install on the NUC once, as root:

```bash
install -m 755 preview-restart-cap.sh /usr/local/bin/
install -m 644 preview-restart-cap.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now preview-restart-cap
journalctl -u preview-restart-cap   # one "capped restarts of ..." line per preview deploy
```

After a host reboot, a preview may stay down until its next deploy: Docker restarts `on-failure`
containers on boot only in some cases.
