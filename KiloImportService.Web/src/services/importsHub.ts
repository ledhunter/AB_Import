/**
 * Тонкая обёртка над SignalR HubConnection для real-time прогресса импорта.
 *
 * Соответствует серверному `ImportProgressHub` (`/hubs/imports`):
 *   - server → client: `StageStarted`, `StageCompleted`, `SessionStatus`, `StageProgress` (TODO)
 *   - client → server: `JoinSession(sessionId)`, `LeaveSession(sessionId)`
 *
 * В dev запросы идут через Vite-proxy `/hubs/imports` (см. vite.config.ts, ws: true).
 */

import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';
import type { ApiImportStageKind, ApiImportStatus } from '../types/api';

const HUB_PATH = '/hubs/imports';
const LOG_TAG = '[ImportsHub]';

export interface StageStartedEvent {
  sessionId: string;
  stage: ApiImportStageKind;
}

export interface StageCompletedEvent {
  sessionId: string;
  stage: ApiImportStageKind;
  rows?: number;
  validRows?: number;
  invalidRows?: number;
  applied?: number;
}

export interface SessionStatusEvent {
  sessionId: string;
  status: ApiImportStatus;
}

/** Прогресс по строкам внутри стадии (Validate/Apply). */
export interface StageProgressEvent {
  sessionId: string;
  stage: ApiImportStageKind;
  currentRow: number;
  totalRows: number;
  percentComplete: number;
  sheet?: string | null;
}

export interface ImportsHubHandlers {
  onStageStarted?: (e: StageStartedEvent) => void;
  onStageCompleted?: (e: StageCompletedEvent) => void;
  onStageProgress?: (e: StageProgressEvent) => void;
  onSessionStatus?: (e: SessionStatusEvent) => void;
  onError?: (err: Error) => void;
}

/**
 * Создаёт и стартует HubConnection. Возвращает объект с методами:
 *   - `joinSession(id)` — подписаться на события сессии
 *   - `leaveSession(id)` — отписаться
 *   - `stop()` — закрыть подключение (cleanup на unmount)
 *
 * Безопасно вызывать `stop()` многократно.
 */
export async function createImportsHub(
  handlers: ImportsHubHandlers = {},
): Promise<{
  connection: HubConnection;
  joinSession: (sessionId: string) => Promise<void>;
  leaveSession: (sessionId: string) => Promise<void>;
  stop: () => Promise<void>;
}> {
  const connection = new HubConnectionBuilder()
    .withUrl(HUB_PATH)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  // ─── Server → client события ───
  if (handlers.onStageStarted) {
    connection.on('StageStarted', (e: StageStartedEvent) => {
      console.info(`${LOG_TAG} ← StageStarted`, e);
      handlers.onStageStarted?.(e);
    });
  }
  if (handlers.onStageCompleted) {
    connection.on('StageCompleted', (e: StageCompletedEvent) => {
      console.info(`${LOG_TAG} ← StageCompleted`, e);
      handlers.onStageCompleted?.(e);
    });
  }
  if (handlers.onSessionStatus) {
    connection.on('SessionStatus', (e: SessionStatusEvent) => {
      console.info(`${LOG_TAG} ← SessionStatus`, e);
      handlers.onSessionStatus?.(e);
    });
  }
  if (handlers.onStageProgress) {
    connection.on('StageProgress', (e: StageProgressEvent) => {
      // StageProgress может приходить ~50 раз — лог в debug-уровне, чтобы не засорять.
      console.debug(`${LOG_TAG} ← StageProgress`, e);
      handlers.onStageProgress?.(e);
    });
  }

  connection.onclose((err) => {
    if (err) {
      console.warn(`${LOG_TAG} connection closed with error:`, err.message);
      handlers.onError?.(err);
    } else {
      console.info(`${LOG_TAG} connection closed`);
    }
  });

  connection.onreconnecting((err) => {
    console.warn(`${LOG_TAG} reconnecting…`, err?.message ?? '');
  });

  connection.onreconnected((id) => {
    console.info(`${LOG_TAG} reconnected (connectionId=${id ?? 'n/a'})`);
  });

  await connection.start();
  console.info(`${LOG_TAG} ✓ connected (state=${connection.state})`);

  return {
    connection,
    joinSession: async (sessionId: string) => {
      if (connection.state !== HubConnectionState.Connected) {
        console.warn(`${LOG_TAG} joinSession пропущен — state=${connection.state}`);
        return;
      }
      console.info(`${LOG_TAG} → JoinSession ${sessionId}`);
      await connection.invoke('JoinSession', sessionId);
    },
    leaveSession: async (sessionId: string) => {
      if (connection.state !== HubConnectionState.Connected) return;
      console.info(`${LOG_TAG} → LeaveSession ${sessionId}`);
      await connection.invoke('LeaveSession', sessionId);
    },
    stop: async () => {
      if (connection.state === HubConnectionState.Disconnected) return;
      try {
        await connection.stop();
      } catch (err) {
        console.warn(
          `${LOG_TAG} stop() error:`,
          err instanceof Error ? err.message : String(err),
        );
      }
    },
  };
}
