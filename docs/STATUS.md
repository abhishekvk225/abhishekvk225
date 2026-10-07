# Status board

Gate legend: ✅ passed · 🔄 in progress · ⬜ not started · ❌ failed. A module is **Complete** only when every gate is ✅ (see Definition of Done in `05-agents-and-roadmap.md`).

| Module | Design | DB | Backend | UI | Security | QA | Code review | DevOps | Final regression | Complete |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| M0 Design docs | 🔄 (awaiting your sign-off) | – | – | – | 🔄 | – | 🔄 | – | – | ⬜ |
| M1 Foundation | ✅ | ✅ (no entities yet) | ✅ | – | 🔄 | 🔄 | 🔄 | 🔄 | ⬜ | ⬜ |
| M2 Identity & Access | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M3 Client Management | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M4 Licensing & Metering | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M5 Face Recognition | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M6 API Management | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M7 Usage & Dashboards | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M8 Blazor Portal polish | ⬜ | – | – | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M9 Hardening | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M10 Release readiness | – | – | – | – | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |

## Environment notes
- .NET 10 SDK installed in the sandbox via apt (`dotnet-sdk-10.0`); SQL Server 2022 container runs through a manually started `dockerd`. NuGet is reachable.
- M1 verified here: solution builds with warnings-as-errors; Domain/Application/Architecture unit tests, Infrastructure tenant/RLS tests (real SQL Server) and API pipeline tests pass.
- **Not verified here**: `deploy/docker/Dockerfile.api` (build containers have no route to NuGet in this sandbox), CI workflows (need to run on GitHub), compose stack.
