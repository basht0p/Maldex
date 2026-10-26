import { ApiError } from '../api/client'
import { messageOf } from '../util/apiMessage'

const TEXT: Record<string, string> = {
  invalid_user_name: 'User names may use letters, digits and . _ @ - (1 to 64 characters).',
  password_too_short: 'Use at least 12 characters.',
  duplicate_user_name: 'That user name is taken.',
  break_glass_requires_local_admin: 'Break-glass is only for local administrator accounts.',
  last_admin: 'Maldex needs at least one administrator who can sign in.',
  role_managed_by_idp: 'Managed by your identity provider',
  concurrent_update: 'Someone else changed this user at the same time. Try again.',
  invalid_password: 'That password was refused. Choose another.',
  password_too_long: 'Use at most 256 characters.',
  invalid_display_name: 'Display names are 1 to 200 characters.',
  invalid_role: 'Choose Admin, Analyst or No role.',
  not_local_user: 'Only local users have a password.',
}

/** Plain-language text for a failed user-admin call; unknown failures fall back to the problem title. */
export function userErrorText(e: unknown): string[] {
  if (e instanceof ApiError && e.code && TEXT[e.code]) return [TEXT[e.code]]
  return messageOf(e)
}
