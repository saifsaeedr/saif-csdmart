/**
 * WebSocket Service for Real-time Communication
 *
 * Compatible with csdmart WebSocket endpoint (GET /ws?token=<jwt>).
 * Supports notification subscriptions via channel-based pub/sub.
 */

import { configReady, website } from "@/config";

export type WebSocketMessage = {
  type: string;
  [key: string]: any;
};

export type ConnectionStatus =
  | "disconnected"
  | "connecting"
  | "connected"
  | "error";

export interface WebSocketCallbacks {
  onMessage?: (data: WebSocketMessage) => void;
  onStatusChange?: (status: ConnectionStatus) => void;
  onError?: (error: Error) => void;
}

export interface SubscribeOptions {
  schema_shortname?: string;
  action_type?: string;
  ticket_state?: string;
}

type MessageListener = (data: WebSocketMessage) => void;
type StatusListener = (status: ConnectionStatus) => void;

export class WebSocketService {
  private ws: WebSocket | null = null;
  private status: ConnectionStatus = "disconnected";
  private reconnectTimer: number | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 3000;
  private callbacks: WebSocketCallbacks = {};
  private token: string;
  private subscriptions: WebSocketMessage[] = [];
  private connectPromise: Promise<boolean> | null = null;
  private messageListeners = new Set<MessageListener>();
  private statusListeners = new Set<StatusListener>();

  constructor(token: string, callbacks: WebSocketCallbacks = {}) {
    this.token = token;
    this.callbacks = callbacks;
  }

  getStatus(): ConnectionStatus {
    return this.status;
  }

  getSubscriptions(): WebSocketMessage[] {
    return [...this.subscriptions];
  }

  setCallbacks(callbacks: WebSocketCallbacks): void {
    this.callbacks = { ...this.callbacks, ...callbacks };
  }

  /**
   * Register a message listener. Returns an unsubscribe function.
   * Multiple listeners can be active simultaneously (unlike setCallbacks).
   */
  addMessageListener(fn: MessageListener): () => void {
    this.messageListeners.add(fn);
    return () => {
      this.messageListeners.delete(fn);
    };
  }

  /**
   * Register a status change listener. Returns an unsubscribe function.
   */
  addStatusListener(fn: StatusListener): () => void {
    this.statusListeners.add(fn);
    return () => {
      this.statusListeners.delete(fn);
    };
  }

  async connect(): Promise<boolean> {
    if (this.status === "connected") {
      return true;
    }

    if (this.connectPromise) {
      return this.connectPromise;
    }

    this.connectPromise = (async () => {
      try {
        console.log("[WebSocket] Starting connection...");
        this.updateStatus("connecting");
        this.clearReconnectTimer();

        await configReady;

        const success = await this.connectWebSocket();
        if (success) {
          return true;
        }

        this.updateStatus("error");
        return false;
      } finally {
        this.connectPromise = null;
      }
    })();

    return this.connectPromise;
  }

  private connectWebSocket(): Promise<boolean> {
    return new Promise((resolve) => {
      try {
        // Derive WS URL from backend: http(s)://host[/base] → ws(s)://host[/base]/ws.
        // The `websocket` field was removed from config; backend is the single
        // source of truth since the WS endpoint always lives at /ws beside the API.
        // When backend is empty (same-origin deployment, e.g. SPA embedded in
        // dmart), fall back to the page's origin instead of crashing on `new
        // URL("")`.
        const backendBase =
          (website.backend?.trim() || (typeof window !== "undefined" ? window.location.origin : "")).replace(/\/+$/, "");
        if (!backendBase) {
          this.updateStatus("disconnected");
          resolve(false);
          return;
        }
        const parsed = new URL(backendBase);
        const wsProtocol = parsed.protocol === "https:" ? "wss:" : "ws:";
        const path = parsed.pathname.replace(/\/+$/, "");
        const wsPath = path.endsWith("/ws") ? path : `${path}/ws`;

        const wsUrl = `${wsProtocol}//${parsed.host}${wsPath}?token=${encodeURIComponent(this.token)}`;

        console.log("[WebSocket] Connecting to", wsUrl);
        this.ws = new WebSocket(wsUrl);

        this.ws.onopen = () => {
          console.log("[WebSocket] Connection open");
          this.updateStatus("connected");
          this.reconnectAttempts = 0;
          this.resendSubscriptions();
          resolve(true);
        };

        this.ws.onmessage = (event) => {
          try {
            const data = JSON.parse(event.data);
            this.handleMessage(data);
          } catch (error) {
            console.error("[WebSocket] Failed to parse message:", error);
          }
        };

        this.ws.onclose = () => {
          console.log("[WebSocket] Connection closed");
          this.ws = null;
          if (this.status === "connecting") {
            resolve(false);
          }
          this.handleDisconnect();
        };

        this.ws.onerror = (event) => {
          console.error("[WebSocket] Error:", event);
          if (this.callbacks.onError) {
            this.callbacks.onError(new Error("WebSocket connection error"));
          }
        };
      } catch (error: any) {
        console.error(
          "[WebSocket] Connection failed:",
          error?.message || error,
        );
        this.cleanup();
        resolve(false);
      }
    });
  }

  private handleDisconnect(): void {
    this.updateStatus("disconnected");
    this.scheduleReconnect();
  }

  private cleanup(): void {
    if (this.ws) {
      try {
        this.ws.close();
      } catch {
        /* ignore */
      }
      this.ws = null;
    }
  }

  async disconnect(): Promise<void> {
    this.clearReconnectTimer();
    this.messageListeners.clear();
    this.statusListeners.clear();
    this.cleanup();
    this.updateStatus("disconnected");
  }

  async send(message: WebSocketMessage): Promise<boolean> {
    if (this.status !== "connected") {
      if (this.connectPromise || this.status === "connecting") {
        console.log("[WebSocket] Send requested while connecting, waiting...");
        const connected = await this.connect();
        if (!connected) return false;
      } else {
        console.warn(
          "[WebSocket] Cannot send, not connected. Status:",
          this.status,
        );
        return false;
      }
    }

    try {
      if (this.ws && this.ws.readyState === WebSocket.OPEN) {
        this.ws.send(JSON.stringify(message));
        return true;
      }

      console.warn("[WebSocket] Socket not open");
      return false;
    } catch (error) {
      console.error("[WebSocket] Send failed:", error);
      this.scheduleReconnect();
      return false;
    }
  }

  /**
   * Subscribe to a notification channel.
   * Note: csdmart allows only ONE subscription per user — each new
   * subscription replaces the previous one on the server.
   * Subpath is normalized to start with "/" to match server channel format.
   */
  async subscribe(
    spaceName: string,
    subpath: string,
    options: SubscribeOptions = {},
  ): Promise<boolean> {
    // Normalize subpath to start with / (server generates channels with / prefix)
    const normalizedSubpath = subpath.startsWith("/")
      ? subpath
      : `/${subpath}`;

    const subscription: WebSocketMessage = {
      type: "notification_subscription",
      space_name: spaceName,
      subpath: normalizedSubpath,
      ...(options.schema_shortname && {
        schema_shortname: options.schema_shortname,
      }),
      ...(options.action_type && { action_type: options.action_type }),
      ...(options.ticket_state && { ticket_state: options.ticket_state }),
    };

    // Track locally (replace existing for same space+subpath)
    const existingIndex = this.subscriptions.findIndex(
      (s) =>
        s.space_name === spaceName && s.subpath === normalizedSubpath,
    );

    if (existingIndex === -1) {
      this.subscriptions.push(subscription);
    } else {
      this.subscriptions[existingIndex] = subscription;
    }

    return await this.send(subscription);
  }

  /**
   * Remove a subscription from local tracking.
   */
  unsubscribe(spaceName: string, subpath: string): void {
    const normalizedSubpath = subpath.startsWith("/")
      ? subpath
      : `/${subpath}`;
    this.subscriptions = this.subscriptions.filter(
      (s) =>
        !(
          s.space_name === spaceName && s.subpath === normalizedSubpath
        ),
    );
  }

  /**
   * Unsubscribe from all channels on the server and clear local tracking.
   */
  async unsubscribeAll(): Promise<boolean> {
    this.subscriptions = [];
    return await this.send({ type: "notification_unsubscribe" });
  }

  async sendChatMessage(
    spaceName: string,
    subpath: string,
    message: string,
    options: {
      senderId?: string;
      receiverId?: string;
      groupId?: string;
      hasAttachments?: boolean;
      attachments?: any[];
    } = {},
  ): Promise<boolean> {
    const msg: WebSocketMessage = {
      type: "chat_message",
      space_name: spaceName,
      subpath: subpath,
      message: message,
      ...options,
    };
    return await this.send(msg);
  }

  async sendBroadcastMessage(
    messageId: string,
    senderId: string,
    content: string,
    options: {
      receiverId?: string;
      groupId?: string;
      timestamp?: string;
      hasAttachments?: boolean;
      attachments?: any[];
      participants?: string[];
    } = {},
  ): Promise<boolean> {
    const msg: WebSocketMessage = {
      type: "message",
      messageId,
      senderId,
      content,
      ...options,
    };
    return await this.send(msg);
  }

  private handleMessage(data: WebSocketMessage): void {
    if (this.callbacks.onMessage) {
      this.callbacks.onMessage(data);
    }
    for (const fn of this.messageListeners) {
      try {
        fn(data);
      } catch (e) {
        console.error("[WebSocket] Listener error:", e);
      }
    }
  }

  private updateStatus(status: ConnectionStatus): void {
    if (this.status !== status) {
      console.log(`[WebSocket] Status: ${this.status} -> ${status}`);
      this.status = status;
      if (this.callbacks.onStatusChange) {
        this.callbacks.onStatusChange(status);
      }
      for (const fn of this.statusListeners) {
        try {
          fn(status);
        } catch (e) {
          console.error("[WebSocket] Status listener error:", e);
        }
      }
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer !== null) return;
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      console.error("[WebSocket] Max reconnection attempts reached");
      this.updateStatus("error");
      return;
    }

    this.updateStatus("disconnected");
    this.reconnectAttempts++;

    console.log(
      `[WebSocket] Reconnecting in ${this.reconnectDelay}ms (attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts})`,
    );

    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null;
      this.cleanup();
      this.connect();
    }, this.reconnectDelay);
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private async resendSubscriptions(): Promise<void> {
    // Only resend the last subscription (csdmart allows one per user)
    if (this.subscriptions.length > 0) {
      const lastSub = this.subscriptions[this.subscriptions.length - 1];
      await this.send(lastSub);
    }
  }
}

let globalWebSocket: WebSocketService | null = null;

export function getWebSocketService(
  token?: string,
  callbacks?: WebSocketCallbacks,
): WebSocketService | null {
  if (!globalWebSocket && token) {
    globalWebSocket = new WebSocketService(token, callbacks);
  } else if (globalWebSocket && callbacks) {
    globalWebSocket.setCallbacks(callbacks);
  }

  return globalWebSocket;
}

export function resetWebSocketService(): void {
  if (globalWebSocket) {
    globalWebSocket.disconnect();
    globalWebSocket = null;
  }
}

export default WebSocketService;
