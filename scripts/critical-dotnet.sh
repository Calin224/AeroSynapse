#!/bin/sh
set -eu
dotnet test backend/tests/AeroSynapse.Tests/AeroSynapse.Tests.csproj -c Release --artifacts-path /critical-tests --no-build --logger 'trx;LogFileName=domain-api.trx' --results-directory /artifacts
dotnet /critical-tests/bin/AeroSynapse.Contract.Tests/release/AeroSynapse.Contract.Tests.dll -parallelMode none -xml /artifacts/contracts.xml
dotnet /critical-tests/bin/AeroSynapse.Management.Tests/release/AeroSynapse.Management.Tests.dll -class '*ScenarioStudioTests' -class '*FlightSchedulingTests' -class '*ComparisonPolicyTests' -class '*StaffingEvaluationPolicyTests' -parallelMode none -xml /artifacts/domain-management.xml
