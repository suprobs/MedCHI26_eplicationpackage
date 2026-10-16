using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Fase 07 — analisis automatico de logs de sesion.
    ///
    /// Lee todos los archivos its_log_*.json que el ITSGuideController ha
    /// exportado a Application.persistentDataPath y calcula las metricas de
    /// calidad de la simulacion. Las metricas de eficiencia del proceso de
    /// desarrollo (tiempo por fase, intervenciones de experto) se registran
    /// en el campo ProcessMetrics de esta clase — esos valores se pasan
    /// manualmente por el investigador porque miden el proceso externo, no
    /// el comportamiento del usuario dentro de la simulacion.
    ///
    /// La salida es un JSON de resultados en la misma carpeta que los logs.
    /// </summary>
    public class SessionLogAnalyzer : MonoBehaviour
    {
        [Header("Metricas del proceso de desarrollo (rellenar manualmente por fase)")]
        [Tooltip("Tiempo real transcurrido en cada fase, en minutos. Clave = nombre de fase.")]
        public List<PhaseTimingEntry> phaseTimings = new List<PhaseTimingEntry>()
        {
            new PhaseTimingEntry { phaseName = "01_parseo", expert_minutes = 0f, ai_turns = 1 },
            new PhaseTimingEntry { phaseName = "02_extraccion", expert_minutes = 0f, ai_turns = 1 },
            new PhaseTimingEntry { phaseName = "03_efsm", expert_minutes = 0f, ai_turns = 1 },
            new PhaseTimingEntry { phaseName = "04_assets3d", expert_minutes = 0f, ai_turns = 3 },
            new PhaseTimingEntry { phaseName = "05_unity", expert_minutes = 0f, ai_turns = 1 },
            new PhaseTimingEntry { phaseName = "06_its", expert_minutes = 0f, ai_turns = 1 },
            new PhaseTimingEntry { phaseName = "07_evaluacion", expert_minutes = 0f, ai_turns = 1 },
        };

        [Header("Umbrales de interpretacion")]
        [Tooltip("Tasa de completado minima esperada para considerar la simulacion funcional.")]
        [Range(0f, 1f)] public float completionRateThreshold = 0.7f;
        [Tooltip("Numero de escaladas a guia simplificada por encima del cual un paso se considera 'problematico'.")]
        public int escalationWarningThreshold = 2;

        [Header("Debug — muestra la ruta real en la consola")]
        public bool logPersistentPath = true;

        [ContextMenu("Analyze All Session Logs")]
        public void AnalyzeAll()
        {
            if (logPersistentPath)
                Debug.Log($"[SessionLogAnalyzer] Buscando logs en: {Application.persistentDataPath}");

            var logFiles = Directory.GetFiles(Application.persistentDataPath, "its_log_*.json");
            if (logFiles.Length == 0)
            {
                Debug.LogWarning("[SessionLogAnalyzer] No se encontraron archivos de log. " +
                                 "Ejecuta la simulacion al menos una vez para generar uno.");
                return;
            }

            Debug.Log($"[SessionLogAnalyzer] {logFiles.Length} sesion(es) encontrada(s).");

            var sessions = new List<SessionLog>();
            foreach (var path in logFiles)
            {
                try
                {
                    var log = JsonUtility.FromJson<SessionLog>(File.ReadAllText(path));
                    sessions.Add(log);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SessionLogAnalyzer] Error al leer {path}: {e.Message}");
                }
            }

            var results = ComputeQualityMetrics(sessions);
            var processResults = ComputeProcessMetrics();
            var output = BuildOutputJson(results, processResults, sessions.Count);

            var outPath = Path.Combine(Application.persistentDataPath,
                $"evaluation_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(outPath, output);
            Debug.Log($"[SessionLogAnalyzer] Informe de evaluacion exportado a: {outPath}");
            LogSummary(results, processResults);
        }

        // ─── METRICAS DE CALIDAD DE LA SIMULACION ───────────────────────────

        private QualityMetrics ComputeQualityMetrics(List<SessionLog> sessions)
        {
            var m = new QualityMetrics();
            m.sessionCount = sessions.Count;

            var completedSessions = sessions.Where(s =>
                s.steps.All(step => step.completedAtSeconds >= 0f)).ToList();
            m.completionRate = sessions.Count > 0
                ? (float)completedSessions.Count / sessions.Count
                : 0f;

            // Tiempo total por sesion (desde entrada al primer paso hasta salida del ultimo)
            var sessionDurations = completedSessions
                .Where(s => s.steps.Count > 0)
                .Select(s => s.steps.Last().completedAtSeconds - s.steps.First().enteredAtSeconds)
                .ToList();
            m.avgSessionDurationSeconds = sessionDurations.Count > 0
                ? sessionDurations.Average() : 0f;
            m.minSessionDurationSeconds = sessionDurations.Count > 0
                ? sessionDurations.Min() : 0f;
            m.maxSessionDurationSeconds = sessionDurations.Count > 0
                ? sessionDurations.Max() : 0f;

            // Fallos por paso (agrupados por numero de paso, promediados entre sesiones)
            var allStepOrders = sessions.SelectMany(s => s.steps).Select(st => st.stepOrder).Distinct().OrderBy(o => o);
            m.failuresByStep = new List<StepFailureMetric>();
            foreach (var order in allStepOrders)
            {
                var stepsAtOrder = sessions
                    .SelectMany(s => s.steps)
                    .Where(st => st.stepOrder == order)
                    .ToList();
                var avgFailures = stepsAtOrder.Count > 0
                    ? (float)stepsAtOrder.Average(st => st.failureCount) : 0f;
                var escalations = stepsAtOrder.Count(st => st.failureCount >= 2);
                m.failuresByStep.Add(new StepFailureMetric
                {
                    stepOrder = order,
                    description = stepsAtOrder.FirstOrDefault()?.description ?? "",
                    avgFailuresPerSession = avgFailures,
                    escalationCount = escalations,
                    isProblematic = escalations >= escalationWarningThreshold
                });
            }

            m.totalItsEscalations = m.failuresByStep.Sum(s => s.escalationCount);
            m.problematicStepCount = m.failuresByStep.Count(s => s.isProblematic);
            m.completionRateMeetsThreshold = m.completionRate >= completionRateThreshold;

            return m;
        }

        // ─── METRICAS DE EFICIENCIA DEL PROCESO ─────────────────────────────

        private ProcessMetrics ComputeProcessMetrics()
        {
            var m = new ProcessMetrics();
            m.totalExpertMinutes = phaseTimings.Sum(p => p.expert_minutes);
            m.totalAiTurns = phaseTimings.Sum(p => p.ai_turns);
            m.phaseBreakdown = phaseTimings.Select(p => new PhaseResult
            {
                phaseName = p.phaseName,
                expertMinutes = p.expert_minutes,
                aiTurns = p.ai_turns,
                automationRatio = p.ai_turns > 0 && p.expert_minutes > 0
                    ? 1f - (p.expert_minutes / (p.expert_minutes + p.ai_turns * 5f))
                    : p.expert_minutes == 0 ? 1f : 0f
            }).ToList();
            return m;
        }

        // ─── SALIDA ──────────────────────────────────────────────────────────

        private string BuildOutputJson(QualityMetrics quality, ProcessMetrics process, int sessionCount)
        {
            return JsonUtility.ToJson(new EvaluationOutput
            {
                generatedAt = DateTime.Now.ToString("o"),
                sessionCount = sessionCount,
                quality = quality,
                process = process
            }, true);
        }

        private void LogSummary(QualityMetrics q, ProcessMetrics p)
        {
            Debug.Log("═══ RESUMEN DE EVALUACION (Fase 07) ═══");
            Debug.Log($"Sesiones analizadas: {q.sessionCount}");
            Debug.Log($"Tasa de completado: {q.completionRate * 100f:F0}% " +
                      $"({(q.completionRateMeetsThreshold ? "ALCANZA" : "NO ALCANZA")} el umbral del {completionRateThreshold * 100f:F0}%)");
            Debug.Log($"Duracion media de sesion: {q.avgSessionDurationSeconds:F0}s");
            Debug.Log($"Escaladas ITS totales: {q.totalItsEscalations} | Pasos problematicos: {q.problematicStepCount}");
            foreach (var s in q.failuresByStep)
                if (s.isProblematic)
                    Debug.LogWarning($"  Paso {s.stepOrder} ('{s.description}'): " +
                                     $"{s.avgFailuresPerSession:F1} fallos/sesion promedio — REVISAR en la iteracion siguiente.");
            Debug.Log($"Tiempo total de experto: {p.totalExpertMinutes:F0} min | Turnos de IA: {p.totalAiTurns}");
        }

        // ─── TIPOS DE DATOS ──────────────────────────────────────────────────

        [Serializable] public class PhaseTimingEntry
        {
            public string phaseName;
            public float expert_minutes;
            public int ai_turns;
        }

        [Serializable] public class QualityMetrics
        {
            public int sessionCount;
            public float completionRate;
            public float avgSessionDurationSeconds;
            public float minSessionDurationSeconds;
            public float maxSessionDurationSeconds;
            public int totalItsEscalations;
            public int problematicStepCount;
            public bool completionRateMeetsThreshold;
            public List<StepFailureMetric> failuresByStep;
        }

        [Serializable] public class StepFailureMetric
        {
            public int stepOrder;
            public string description;
            public float avgFailuresPerSession;
            public int escalationCount;
            public bool isProblematic;
        }

        [Serializable] public class ProcessMetrics
        {
            public float totalExpertMinutes;
            public int totalAiTurns;
            public List<PhaseResult> phaseBreakdown;
        }

        [Serializable] public class PhaseResult
        {
            public string phaseName;
            public float expertMinutes;
            public int aiTurns;
            public float automationRatio;
        }

        [Serializable] public class EvaluationOutput
        {
            public string generatedAt;
            public int sessionCount;
            public QualityMetrics quality;
            public ProcessMetrics process;
        }
    }
}