import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { userErrorText } from './userErrors'

describe('userErrorText', () => {
  // The user-admin API answers {error} without a title, so every code it can return needs its own text.
  it.each([
    [409, 'concurrent_update', 'Someone else changed this user at the same time. Try again.'],
    [400, 'invalid_password', 'That password was refused. Choose another.'],
    [400, 'password_too_long', 'Use at most 256 characters.'],
    [400, 'invalid_display_name', 'Display names are 1 to 200 characters.'],
    [400, 'invalid_role', 'Choose Admin, Analyst or No role.'],
    [400, 'not_local_user', 'Only local users have a password.'],
    [409, 'duplicate_user_name', 'That user name is taken.'],
  ])('%i %s has its own text', (status, error, text) => {
    expect(userErrorText(new ApiError(status, { error }))).toEqual([text])
  })

  it('never falls back to the bare status for a known code', () => {
    for (const error of ['concurrent_update', 'invalid_password', 'password_too_long', 'invalid_display_name', 'invalid_role', 'not_local_user'])
      expect(userErrorText(new ApiError(400, { error })).join(' ')).not.toMatch(/Request failed with status/)
  })
})
