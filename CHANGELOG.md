# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Initial SDK implementation with Config service client
- `SmplkitClient` top-level entry point with `IDisposable` support
- `ConfigClient` with `GetAsync`, `GetByKeyAsync`, `ListAsync`, `CreateAsync`, `DeleteAsync`
- Typed exception hierarchy: `SmplException`, `SmplConnectionException`, `SmplTimeoutException`, `SmplNotFoundException`, `SmplConflictException`, `SmplValidationException`
- JSON:API response envelope parsing
- Bearer token authentication
- `CancellationToken` support on all async methods
- xUnit test suite with MockHttpMessageHandler
