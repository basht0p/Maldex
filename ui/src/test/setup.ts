import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(() => cleanup())

// jsdom has no object URLs; authenticated images and downloads need them. Tests may spy on these.
let objectUrls = 0
if (typeof URL.createObjectURL !== 'function') URL.createObjectURL = () => `blob:test/${++objectUrls}`
if (typeof URL.revokeObjectURL !== 'function') URL.revokeObjectURL = () => undefined
