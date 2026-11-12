import { useQuery } from '@tanstack/react-query'
import { useOem } from '../OemContext'

/** Company staff a vendor may notify when sending files (the vendor's notification list, names only). */
export function useRecipientChoices() {
  const { api, realm, queryScope } = useOem()
  return useQuery({
    queryKey: ['oem', ...queryScope, 'recipient-choices'],
    queryFn: () => api.transferRecipientChoices(),
    enabled: realm === 'oem',
    retry: false,
  })
}
