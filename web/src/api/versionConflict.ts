import axios from 'axios'

/** 409/40903 is a client-supplied version or compare-and-swap failure. Other 40901 conflicts stay 40901. */
export function isVersionConflict(error: unknown): boolean {
  if (!axios.isAxiosError<{ code?: number }>(error)) return false
  return error.response?.data?.code === 40903
}
