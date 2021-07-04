#!/bin/bash
# Copyright (c) OPC Foundation. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.

BUILDROOT=$(pwd)/..
cd $BUILDROOT

FRAMEWORK=netcoreapp3.1

rm -r ./CodeCoverage
rm -r ./TestResults
dotnet test "UA Core Library.sln" -v n --configuration Release --framework "$(FRAMEWORK)" --collect:"XPlat Code Coverage"  --settings ./Tests/coverlet.runsettings.xml --results-directory ./TestResults

# ensure latest report tool is installed
dotnet tool uninstall -g dotnet-reportgenerator-globaltool
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./CodeCoverage -reporttypes:"Badges;Html;HtmlSummary;Cobertura"

# Display result in browser (mac OS)
open ./CodeCoverage/index.html