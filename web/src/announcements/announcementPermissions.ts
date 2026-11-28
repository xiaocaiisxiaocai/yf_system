export const ANNOUNCEMENT_PERMISSIONS = {
  publish: 'announcement:publish',
  withdraw: 'announcement:withdraw',
  pin: 'announcement:pin',
  reads: 'announcement:reads',
} as const

export const ANNOUNCEMENT_MANAGEMENT_PERMISSIONS = Object.values(ANNOUNCEMENT_PERMISSIONS)
