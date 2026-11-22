// The machine the numbers were measured on, for the /lab page: CPU, memory, OS, and what Docker was allowed to use.
import os from 'node:os';
import { execFileSync } from 'node:child_process';

const docker = (() => {
  try {
    const [cpus, mem, version] = execFileSync('docker', ['info', '--format', '{{.NCPU}}|{{.MemTotal}}|{{.ServerVersion}}'], { encoding: 'utf8' }).trim().split('|');
    return { cpus: Number(cpus), memoryGiB: Math.round((Number(mem) / 1024 ** 3) * 10) / 10, version };
  } catch {
    return null;
  }
})();

const k6 = (() => {
  try {
    return execFileSync('k6', ['version'], { encoding: 'utf8' }).trim().split(' ').slice(0, 2).join(' ');
  } catch {
    return null;
  }
})();

const cpu = os.cpus();
process.stdout.write(
  JSON.stringify(
    {
      cpu: cpu[0]?.model.trim() ?? 'unknown',
      threads: cpu.length,
      memoryGiB: Math.round((os.totalmem() / 1024 ** 3) * 10) / 10,
      os: `${os.type()} ${os.release()}`,
      docker,
      k6,
      note: 'One laptop runs everything: the compose stack in Docker Desktop and k6 on the host, so they share the CPU.',
    },
    null,
    2,
  ),
);
