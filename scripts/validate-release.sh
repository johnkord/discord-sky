#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
export STEWARD_TEST_ROOT=${STEWARD_TEST_ROOT:-"$root/../discord-steward"}
export SKY_REQUIRE_STEWARD=1
pin=$(<"$root/scripts/steward-ref")
[[ $(git -C "$STEWARD_TEST_ROOT" rev-parse HEAD) == "$pin" ]] || { echo 'Wrong Steward revision.' >&2; exit 1; }
[[ -z $(git -C "$STEWARD_TEST_ROOT" status --porcelain --untracked-files=all -- src config/profiles/unrestricted-autonomy.example.json '*.props' '*.targets' global.json NuGet.Config) ]] || { echo 'Dirty Steward fixture.' >&2; exit 1; }
results=${RESULTS_DIRECTORY:-$(mktemp -d)}
mkdir -p "$results"
[[ ! -e "$results/release.trx" ]] || { echo 'Use a fresh results directory for this release.' >&2; exit 1; }
bash scripts/test-rollout.sh
python3 -B -m unittest discover -s scripts -p 'test_inspect_turn.py'
audit=(-p:NuGetAudit=true -p:NuGetAuditMode=all '-p:WarningsAsErrors=NU1900%3BNU1903%3BNU1904%3BNU1905')
dotnet build "$STEWARD_TEST_ROOT/src/DiscordSteward/DiscordSteward.csproj" -c Debug "${audit[@]}"
test -s "$STEWARD_TEST_ROOT/src/DiscordSteward/bin/Debug/net10.0/DiscordSteward.dll"
dotnet restore DiscordSky.sln --force-evaluate "${audit[@]}"
dotnet build DiscordSky.sln -c Release --no-restore
dotnet test tests/DiscordSky.Tests/DiscordSky.Tests.csproj -c Release --no-build --no-restore \
  --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=release.trx' --results-directory "$results"
python3 - "$results/release.trx" <<'PY'
import sys
from xml.etree import ElementTree
root = ElementTree.parse(sys.argv[1])
cases = root.findall('.//{*}UnitTestResult')
required = {'SiblingSteward_UnrestrictedProfileExposesOneReusableCompleteNativeCatalog',
            'SiblingSteward_AutonomyProviderRejectionReturnsTruthfulFailure',
            'SiblingSteward_AbandonedCallsReconcileWithoutReplayingDiscordWrites',
            'SiblingSteward_NaturalRepliesShareOnlyTheExactParentInTheUserTurn',
            'SiblingSteward_DirectDeliveryFaultsCannotHandAPostedTurnToFallback'}
passed = {case.get('testName', '').split('.')[-1] for case in cases if case.get('outcome') == 'Passed'}
if not required <= passed:
    sys.exit('Required Steward test missing or not passed: ' + ', '.join(sorted(required - passed)))
PY
printf 'Release results: %s/release.trx\n' "$results"