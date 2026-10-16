# Contributing to MedSpravki REU

Thank you for helping improve MedSpravki REU. Keep changes focused and use synthetic medical and
student records in every test, screenshot, and example.

## Start with an Issue

Search the [issue tracker](https://github.com/Archangel-gabri/medspravki-reu/issues) first. Open
an issue for a bug or proposal so its scope, role model, and data impact can be agreed.

## Local Setup

Install the .NET 8 SDK and Docker, then restore the solution and start a local PostgreSQL container:

```bash
git clone https://github.com/Archangel-gabri/medspravki-reu.git
cd medspravki-reu
dotnet restore app/ReuMedCertificates.sln
docker run --name reu-pg -d -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=reu_med_certificates -p 5432:5432 postgres:16
```

Start the application with synthetic seed data and a local-only password:

```bash
ASPNETCORE_ENVIRONMENT=Development BootstrapUser__Enabled=false SeedDemoData=true \
DEMO_USER_PASSWORD='choose-a-local-demo-password' Recognition__Provider=Manual \
dotnet run --project app/src/ReuMedCertificates.Web --urls http://localhost:5080
```

Open `http://localhost:5080` and sign in as `teacher` with that local password.

## Tests and Code Checks

With dependencies already restored, run:

```bash
dotnet test app/tests/ReuMedCertificates.UnitTests/ReuMedCertificates.UnitTests.csproj \
  --no-restore --verbosity minimal
dotnet build app/src/ReuMedCertificates.Web/ReuMedCertificates.Web.csproj \
  --no-restore --verbosity minimal
```

No separate linter is configured. Treat compiler and analyzer diagnostics as code-check failures.

## Code Style

- Follow the existing C# 12, nullable-reference, Razor Pages, and clean-architecture patterns.
- Keep domain rules out of page handlers and infrastructure details out of the domain project.
- Preserve role checks, human review of recognition output, audit logging, and file protections.
- Avoid unrelated formatting, generated migrations, or package changes in a focused pull request.

## Propose a Change

1. Agree the scope in an issue.
2. Create a branch such as `fix/certificate-review` from `main`.
3. Make the smallest complete change and add or update tests and documentation.
4. Run the relevant checks and review the complete diff.
5. Open a pull request with the issue link, test output, and screenshots if the UI changed.

## Data and Secrets

Never submit real medical certificates, scans, student records, personal data, credentials,
tokens, `.env` files, database files, SQL dumps, backups, or production logs.

## Security Vulnerabilities

Do not open a public issue for a suspected vulnerability. Use GitHub Security's
[Report a vulnerability](https://github.com/Archangel-gabri/medspravki-reu/security/advisories/new)
form and provide safe reproduction steps without real medical or personal data.
