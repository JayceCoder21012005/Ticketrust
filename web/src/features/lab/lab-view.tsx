import { CheckCircle2, XCircle } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { t } from "@/lib/i18n.server";
import type { LabResults, LabScenario } from "./types";

const DASH = "—";

const ms = (n: number | null | undefined) => (n === null || n === undefined ? DASH : `${n.toLocaleString("vi-VN", { maximumFractionDigits: 1 })} ms`);
const num = (n: number | null | undefined) => (n === null || n === undefined ? DASH : n.toLocaleString("vi-VN", { maximumFractionDigits: 1 }));

function Verdict({ ok }: { ok: boolean }) {
  const Icon = ok ? CheckCircle2 : XCircle;
  return (
    <span className={`inline-flex items-center gap-1 font-medium ${ok ? "text-success" : "text-danger"}`}>
      <Icon className="size-4" aria-hidden />
      {ok ? t("lab.met") : t("lab.missed")}
    </span>
  );
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-3" aria-labelledby={`lab-${title}`}>
      <div className="flex flex-col gap-1">
        <h2 id={`lab-${title}`} className="text-xl font-semibold tracking-tight">
          {title}
        </h2>
        {hint ? <p className="text-sm text-muted-foreground">{hint}</p> : null}
      </div>
      {children}
    </section>
  );
}

function ScenarioRow({ scenario }: { scenario: LabScenario }) {
  if (!scenario.ran) {
    return (
      <TableRow>
        <TableCell className="font-medium">{`${scenario.id} · ${scenario.title}`}</TableCell>
        <TableCell colSpan={4} className="text-muted-foreground">
          {t("lab.notRun")}
        </TableCell>
        <TableCell className="text-muted-foreground">{scenario.budget}</TableCell>
      </TableRow>
    );
  }

  const met = scenario.thresholds.every((x) => x.ok);
  return (
    <TableRow>
      <TableCell className="align-top">
        <div className="font-medium">{`${scenario.id} · ${scenario.title}`}</div>
        <div className="text-xs text-muted-foreground">{scenario.shape}</div>
      </TableCell>
      <TableCell className="align-top tabular-nums">
        {t("lab.scale", "vi", { vus: num(scenario.scale.vusMax), iterations: num(scenario.scale.iterations), seconds: num(scenario.scale.seconds) })}
      </TableCell>
      <TableCell className="align-top tabular-nums">{num(scenario.rps)}</TableCell>
      <TableCell className="align-top tabular-nums">
        {scenario.latency.length === 0
          ? DASH
          : scenario.latency.map((l) => (
              <div key={l.label}>{`${l.label}: ${ms(l.p50)} / ${ms(l.p95)} / ${ms(l.p99)}`}</div>
            ))}
      </TableCell>
      <TableCell className="align-top tabular-nums">
        {t("lab.errorCounts", "vi", { unexpected: num(scenario.errors.unexpected), limited: num(scenario.errors.rateLimited) })}
      </TableCell>
      <TableCell className="align-top">
        <div className="text-xs text-muted-foreground">{scenario.budget}</div>
        <Verdict ok={met} />
        {scenario.thresholds
          .filter((x) => !x.ok)
          .map((x) => (
            <div key={`${x.metric}${x.rule}`} className="text-xs text-danger">{`${x.metric}: ${x.rule}`}</div>
          ))}
      </TableCell>
    </TableRow>
  );
}

/** `/lab` (spec 17): the latest load-test run as it came out of load-tests/collect-results.mjs. Static: no client JavaScript. */
export function LabView({ results }: { results: LabResults }) {
  const ran = results.scenarios.filter((s) => s.ran);
  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-10 px-4 py-6 sm:py-10">
      <header className="flex flex-col gap-2">
        <h1 className="text-2xl font-bold tracking-tight sm:text-3xl">{t("lab.title")}</h1>
        <p className="max-w-3xl text-muted-foreground">{t("lab.intro")}</p>
        {results.generatedAt ? (
          <p className="text-sm text-muted-foreground">
            {t("lab.generated", "vi", {
              at: new Date(results.generatedAt).toLocaleString("vi-VN", { dateStyle: "medium", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh" }),
              commit: results.commit ?? DASH,
            })}
          </p>
        ) : (
          <p className="text-sm text-muted-foreground">{t("lab.noResults")}</p>
        )}
      </header>

      {results.machine ? (
        <Section title={t("lab.machine")} hint={results.machine.note}>
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
            <dt className="text-muted-foreground">CPU</dt>
            <dd>{`${results.machine.cpu} · ${results.machine.threads} ${t("lab.threads")}`}</dd>
            <dt className="text-muted-foreground">RAM</dt>
            <dd>{`${num(results.machine.memoryGiB)} GiB`}</dd>
            <dt className="text-muted-foreground">Docker</dt>
            <dd>{results.machine.docker ? `${results.machine.docker.cpus} CPU · ${num(results.machine.docker.memoryGiB)} GiB · ${results.machine.docker.version}` : DASH}</dd>
            <dt className="text-muted-foreground">{t("lab.os")}</dt>
            <dd>{results.machine.os}</dd>
            <dt className="text-muted-foreground">k6</dt>
            <dd>{results.machine.k6 ?? DASH}</dd>
          </dl>
          {results.scaleNote ? <p className="text-sm text-muted-foreground">{results.scaleNote}</p> : null}
        </Section>
      ) : null}

      <Section title={t("lab.scenarios")} hint={t("lab.scenariosHint")}>
        <div className="overflow-x-auto rounded-xl border">
          <Table>
            <caption className="sr-only">{t("lab.scenarios")}</caption>
            <TableHeader>
              <TableRow>
                <TableHead>{t("lab.columns.scenario")}</TableHead>
                <TableHead>{t("lab.columns.scale")}</TableHead>
                <TableHead>RPS</TableHead>
                <TableHead>{t("lab.columns.latency")}</TableHead>
                <TableHead>{t("lab.columns.errors")}</TableHead>
                <TableHead>{t("lab.columns.budget")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {results.scenarios.map((s) => (
                <ScenarioRow key={s.id} scenario={s} />
              ))}
            </TableBody>
          </Table>
        </div>
      </Section>

      <Section title={t("lab.invariants")} hint={t("lab.invariantsHint")}>
        {ran.length === 0 ? (
          <p className="text-sm text-muted-foreground">{DASH}</p>
        ) : (
          <ul className="grid gap-2 sm:grid-cols-2">
            {ran.map((s) => (
              <li key={s.id} className="flex flex-col gap-1 rounded-xl border p-3">
                <span className="font-medium">{`${s.id} · ${s.title}`}</span>
                {s.invariants.length === 0 ? (
                  <span className="text-sm text-muted-foreground">{t("lab.notChecked")}</span>
                ) : (
                  s.invariants.map((v) => (
                    <span key={v.phase} className="flex flex-wrap items-center gap-2 text-sm">
                      <Badge variant="outline">{t(`lab.phase.${v.phase === "settled" ? "settled" : "after"}`)}</Badge>
                      <span className={v.ok ? "text-success" : "text-danger"} aria-label={v.ok ? t("lab.holds") : t("lab.violated")}>
                        {v.ok ? "✓" : "✗"}
                      </span>
                      <span>{v.ok ? t("lab.holds") : v.note ?? t("lab.violations", "vi", { count: String(v.violations.length) })}</span>
                      {v.redis ? <span className={v.redis.ok ? "text-success" : "text-danger"}>{v.redis.ok ? t("lab.redisOk") : t("lab.redisDiffers")}</span> : null}
                    </span>
                  ))
                )}
              </li>
            ))}
          </ul>
        )}
      </Section>

      {results.soak ? (
        <Section title={t("lab.soak")} hint={t("lab.soakHint")}>
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
            <dt className="text-muted-foreground">{t("lab.soakApi")}</dt>
            <dd className="tabular-nums">{`${num(results.soak.apiMiBPer10Min)} MiB / 10 ${t("lab.minutes")}`}</dd>
            <dt className="text-muted-foreground">{t("lab.soakWorker")}</dt>
            <dd className="tabular-nums">{`${num(results.soak.workerMiBPer10Min)} MiB / 10 ${t("lab.minutes")}`}</dd>
            <dt className="text-muted-foreground">{t("lab.soakGc")}</dt>
            <dd className="tabular-nums">{`${num(results.soak.gcHeapMiBPer10Min)} MiB / 10 ${t("lab.minutes")}`}</dd>
            <dt className="text-muted-foreground">{t("lab.soakHolds")}</dt>
            <dd className="tabular-nums">{t("lab.soakHoldsValue", "vi", { peak: num(results.soak.holdKeysPeak), end: num(results.soak.holdKeysEnd) })}</dd>
            <dt className="text-muted-foreground">{t("lab.verdict")}</dt>
            <dd>
              <Verdict ok={results.soak.memoryStable && results.soak.holdKeysBackToBaseline} />
            </dd>
          </dl>
        </Section>
      ) : null}

      {results.chaos.length > 0 ? (
        <Section title={t("lab.chaos")}>
          <ol className="flex flex-col gap-1 text-sm">
            {results.chaos.map((f) => (
              <li key={`${f.at}${f.fault}`} className="tabular-nums">{`${f.at} s · ${t(`lab.faults.${f.fault}`)} (${f.detail})`}</li>
            ))}
          </ol>
        </Section>
      ) : null}

      {results.trace?.complete ? (
        <Section title={t("lab.trace")} hint={t("lab.traceHint", "vi", { spans: String(results.trace.spanCount), services: results.trace.services.join(", ") })}>
          <ol className="flex flex-col gap-1 text-sm">
            {results.trace.steps.map((s) => (
              <li key={s.step} className="grid grid-cols-[4.5rem_4rem_1fr] gap-2 tabular-nums">
                <span className="text-muted-foreground">{`+${num(s.atMs)} ms`}</span>
                <span className="text-muted-foreground">{s.service}</span>
                <span className="break-all">{`${s.name} · ${ms(s.durationMs)}`}</span>
              </li>
            ))}
          </ol>
        </Section>
      ) : null}

      {results.brokeAndFixed.length > 0 ? (
        <Section title={t("lab.broke")}>
          <ul className="flex flex-col gap-3">
            {results.brokeAndFixed.map((b) => (
              <li key={b.title} className="flex flex-col gap-1 rounded-xl border p-4">
                <span className="font-medium">{b.title}</span>
                <span className="text-sm">{b.what}</span>
                <span className="text-sm text-muted-foreground">{b.fix}</span>
              </li>
            ))}
          </ul>
        </Section>
      ) : null}
    </div>
  );
}
