# Contributing to Autopilot Monitor

Thank you for your interest in contributing to Autopilot Monitor! This document provides guidelines and instructions for contributing.

## Code of Conduct

Be respectful, professional, and constructive. We're all here to make Autopilot deployments better.

## How Can I Contribute?

### Reporting Bugs

Before submitting a bug report:
1. Check existing GitHub issues
2. Verify you're using the latest version
3. Collect relevant logs and error messages

**Good Bug Report Includes**:
- Clear description of the issue
- Steps to reproduce
- Expected vs actual behavior
- Environment details (OS version, .NET version, etc.)
- Relevant log excerpts
- Screenshots if applicable

### Suggesting Features

We love feature ideas! Before submitting:
1. Check if it's already been suggested
2. Consider if it fits the project scope
3. Think about implementation complexity

**Good Feature Request Includes**:
- Clear description of the feature
- Use case / problem it solves
- Proposed solution (if you have one)
- Alternative approaches considered

### Contributing Code

#### Development Setup

1. Fork the repository
2. Clone your fork
3. Follow the [Getting Started Guide](docs/getting-started.md)
4. Create a feature branch: `git checkout -b feature/my-feature`

#### Coding Standards

**C# / .NET**
- Follow Microsoft C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and small (<50 lines preferred)
- Use async/await properly (don't block)

**TypeScript / React**
- Use TypeScript for type safety
- Follow React best practices (hooks, functional components)
- Use meaningful component and variable names
- Keep components small and focused
- Extract reusable logic into custom hooks

**PowerShell**
- Use approved verbs (Get-, Set-, New-, etc.)
- Add comment-based help for scripts
- Use meaningful parameter names
- Include error handling

#### Code Review Process

1. Ensure all tests pass
2. Update documentation if needed
3. Submit a pull request with:
   - Clear title and description
   - Reference to related issues
   - Screenshots (if UI changes)
4. Address review feedback
5. Squash commits before merge (if requested)

### Contributing Rules

The **rule engine** (Phase 2+) is designed to be community-driven. You can contribute rules to help diagnose common issues.

#### Rule Structure

Rules are YAML files in the `rules/` directory:

```yaml
id: NET-001
title: "Proxy Authentication Required"
severity: high
category: network
version: 1.0
author: "Your Name"
description: |
  Detects when a proxy requires authentication but SYSTEM account cannot authenticate.

conditions:
  - signal: "event_407_detected"
    source: "windows_events"
    provider: "Microsoft-Windows-WinHttp"
    event_id: 407
    minimum_occurrences: 1

evidence_selectors:
  - type: "log_lines"
    source: "ime_log"
    pattern: "proxy|407|authentication"
    context_lines: 3

explanation: |
  The device is behind a proxy that requires authentication, but the SYSTEM
  account (used during enrollment) cannot authenticate.

remediation:
  - "Configure proxy bypass for *.manage.microsoft.com"
  - "Configure proxy bypass for *.microsoftonline.com"
  - "Use PAC file with proper exclusions"

related_docs:
  - "https://learn.microsoft.com/en-us/mem/intune/enrollment/enrollment-autopilot"

confidence_scoring:
  base: 50
  factors:
    - signal: "winhttp_proxy_configured"
      weight: 20
    - signal: "enrollment_stalled_at_mdm"
      weight: 30
```

#### Submitting Rules

1. Create a new YAML file in the appropriate `rules/` subdirectory
2. Follow the schema above
3. Test your rule against sample data
4. Submit a PR with:
   - The rule file
   - Test data (anonymized)
   - Explanation of how to trigger this condition

## Project Structure

```
Autopilot-Monitor/
├── src/
│   ├── Agent/              # Windows monitoring agent
│   ├── Backend/            # Azure Functions API
│   ├── Shared/             # Common models/contracts
│   └── Web/                # Next.js web UI
├── scripts/
│   ├── Bootstrap/          # Intune deployment scripts
│   └── Deployment/         # Azure deployment scripts
├── rules/                  # Community rule definitions
├── docs/                   # Documentation
└── tests/                  # Tests (to be added)
```

## Testing Guidelines

### Unit Tests
- Test core logic in isolation
- Mock external dependencies
- Aim for >80% code coverage on critical paths

### Integration Tests
- Test API endpoints with real Azure Storage Emulator
- Verify agent can upload to Functions
- Test error handling and retries

### End-to-End Tests
- Test full enrollment simulation (when feasible)
- Verify UI displays data correctly
- Test troubleshooting workflows

## Documentation

When adding features, please update:
- **README.md** - If it affects setup or high-level description
- **docs/getting-started.md** - If it affects the getting started process
- **docs/architecture.md** - If it changes architecture
- **Inline code comments** - For complex logic
- **XML docs** - For public APIs

## Git Workflow

We use a simplified Git workflow:

1. **main** branch - Stable, production-ready code
2. **feature/** branches - New features in development
3. **bugfix/** branches - Bug fixes
4. **hotfix/** branches - Critical production fixes

### Commit Messages

Use clear, descriptive commit messages:

```
[Component] Short description

Longer explanation if needed.

Fixes #123
```

Examples:
- `[Agent] Add support for pre-provisioning scenarios`
- `[Web] Implement session detail page with timeline`
- `[Functions] Fix race condition in event ingestion`
- `[Docs] Update architecture diagram`

## Release Process

Releases follow semantic versioning (MAJOR.MINOR.PATCH):

- **MAJOR**: Breaking changes
- **MINOR**: New features (backwards compatible)
- **PATCH**: Bug fixes

### Release Checklist

- [ ] All tests pass
- [ ] Documentation updated
- [ ] CHANGELOG.md updated
- [ ] Version numbers incremented
- [ ] Tag created: `vX.Y.Z`
- [ ] GitHub release published
- [ ] Deployment artifacts published

## Getting Help

- **Questions**: Open a GitHub Discussion
- **Bugs**: Open a GitHub Issue
- **Feature Ideas**: Open a GitHub Issue with "enhancement" label
- **Security Issues**: Email security@yourcompany.com (do NOT open public issue)

## Recognition

Contributors will be:
- Listed in CONTRIBUTORS.md
- Credited in release notes
- Mentioned in documentation for significant contributions

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

## Thank You!

Your contributions make Autopilot Monitor better for everyone. Thank you for taking the time to contribute!
