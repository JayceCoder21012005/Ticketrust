# Spec 17: `make load-test` runs every load-test scenario against the docker-compose stack (needs Docker, k6, Node, bash).
# On Windows without make, run `bash load-tests/run.sh` from Git Bash. Knobs: see the header of load-tests/run.sh.
.PHONY: load-test load-test-quick

load-test:
	bash load-tests/run.sh

# A few minutes instead of an hour: small scale, short soak. Proves the pipeline, not the system.
load-test-quick:
	SPIKE_VUS=200 BUY_BUYERS=200 STORM_BUYERS=50 STORM_MAX_DELAY=20 SOAK_DURATION=2m CHAOS_BUYERS=200 CHAOS_AFTER=30 SETTLE_SECONDS=60 bash load-tests/run.sh
