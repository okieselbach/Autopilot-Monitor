"use client";

import { useState, useCallback, useRef, useEffect } from "react";

export interface UseNotificationMessagesOptions {
  /** Auto-dismiss timeout for success messages in ms. Default: 3000 */
  successTimeout?: number;
  /** Auto-dismiss timeout for error messages in ms. Default: 5000 */
  errorTimeout?: number;
}

export interface UseNotificationMessagesReturn {
  successMessage: string | null;
  error: string | null;
  showSuccess: (message: string, timeout?: number) => void;
  showError: (message: string, timeout?: number) => void;
  clearSuccess: () => void;
  clearError: () => void;
  clearAll: () => void;
}

export function useNotificationMessages(
  options?: UseNotificationMessagesOptions,
): UseNotificationMessagesReturn {
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const successTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const errorTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (successTimerRef.current) clearTimeout(successTimerRef.current);
      if (errorTimerRef.current) clearTimeout(errorTimerRef.current);
    };
  }, []);

  const clearSuccess = useCallback(() => {
    setSuccessMessage(null);
    if (successTimerRef.current) {
      clearTimeout(successTimerRef.current);
      successTimerRef.current = null;
    }
  }, []);

  const clearError = useCallback(() => {
    setError(null);
    if (errorTimerRef.current) {
      clearTimeout(errorTimerRef.current);
      errorTimerRef.current = null;
    }
  }, []);

  const showSuccess = useCallback(
    (message: string, timeout?: number) => {
      if (successTimerRef.current) clearTimeout(successTimerRef.current);
      setSuccessMessage(message);
      const ms = timeout ?? options?.successTimeout ?? 3000;
      successTimerRef.current = setTimeout(() => {
        setSuccessMessage(null);
        successTimerRef.current = null;
      }, ms);
    },
    [options?.successTimeout],
  );

  const showError = useCallback(
    (message: string, timeout?: number) => {
      if (errorTimerRef.current) clearTimeout(errorTimerRef.current);
      setError(message);
      const ms = timeout ?? options?.errorTimeout ?? 5000;
      errorTimerRef.current = setTimeout(() => {
        setError(null);
        errorTimerRef.current = null;
      }, ms);
    },
    [options?.errorTimeout],
  );

  const clearAll = useCallback(() => {
    clearSuccess();
    clearError();
  }, [clearSuccess, clearError]);

  return {
    successMessage,
    error,
    showSuccess,
    showError,
    clearSuccess,
    clearError,
    clearAll,
  };
}
