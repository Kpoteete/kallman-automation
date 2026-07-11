# Security

Do not report vulnerabilities in public issues. Contact the repository owner privately.

Momentus credentials must be supplied through `MOMENTUS_APIUSER`, `MOMENTUS_SECRET`, and
`MOMENTUS_KEY`. Never add fallback credentials to source, scripts, documentation, fixtures,
or generated artifacts. Treat diagnostic output and audit logs as sensitive business data.

If a secret is committed, removing it from the current file is not sufficient. Revoke it,
review access logs, remove it from Git history using an agreed history-rewrite procedure,
and enable GitHub secret scanning and push protection.
