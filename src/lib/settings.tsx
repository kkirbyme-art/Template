/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from "react"

export interface ThemePreset {
    name: string
    label: string
    primary: string        // oklch value for --primary (light)
    primaryDark: string    // oklch value for --primary (dark)
}

export const themePresets: ThemePreset[] = [
    { name: "sky", label: "Sky", primary: "oklch(0.59 0.14 242)", primaryDark: "oklch(0.68 0.15 237)" },
    { name: "blue", label: "Blue", primary: "oklch(0.55 0.20 255)", primaryDark: "oklch(0.65 0.20 255)" },
    { name: "violet", label: "Violet", primary: "oklch(0.55 0.22 285)", primaryDark: "oklch(0.65 0.22 285)" },
    { name: "rose", label: "Rose", primary: "oklch(0.58 0.22 350)", primaryDark: "oklch(0.68 0.22 350)" },
    { name: "orange", label: "Orange", primary: "oklch(0.65 0.20 50)", primaryDark: "oklch(0.70 0.18 50)" },
    { name: "green", label: "Green", primary: "oklch(0.55 0.17 155)", primaryDark: "oklch(0.65 0.17 155)" },
    { name: "amber", label: "Amber", primary: "oklch(0.65 0.18 80)", primaryDark: "oklch(0.70 0.16 80)" },
    { name: "slate", label: "Slate", primary: "oklch(0.45 0.02 260)", primaryDark: "oklch(0.65 0.02 260)" },
    { name: "red", label: "Red", primary: "oklch(0.58 0.22 27)", primaryDark: "oklch(0.70 0.19 22)" },
    { name: "teal", label: "Teal", primary: "oklch(0.55 0.12 185)", primaryDark: "oklch(0.65 0.12 185)" },
]

interface SettingsData {
    logo: string          // base64 data URL or empty
    themeName: string     // preset name
    orgAddress: string    // organization address
}

interface SettingsContextType extends SettingsData {
    setLogo: (logo: string) => void
    setThemeName: (name: string) => void
    setOrgAddress: (address: string) => void
}

const SETTINGS_KEY = "dgsign_settings"

const defaults: SettingsData = {
    logo: "",
    themeName: "sky",
    orgAddress: "",
}

function loadSettings(): SettingsData {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY)
        if (!raw) return defaults
        return { ...defaults, ...JSON.parse(raw) }
    } catch {
        return defaults
    }
}

function persistSettings(data: SettingsData) {
    try {
        localStorage.setItem(SETTINGS_KEY, JSON.stringify(data))
    } catch {
        // localStorage quota exceeded — silently ignore
    }
}

function applyTheme(themeName: string) {
    const preset = themePresets.find((p) => p.name === themeName) ?? themePresets[0]
    const root = document.documentElement
    root.style.setProperty("--primary", preset.primary)
    root.style.setProperty("--sidebar-primary", preset.primary)
    root.style.setProperty("--chart-4", preset.primary)
    // We'll keep dark mode in sync via a class-based check
    // For simplicity, override both — the CSS custom props cascade normally
}

const SettingsContext = createContext<SettingsContextType | null>(null)

export function SettingsProvider({ children }: { children: ReactNode }) {
    const [settings, setSettings] = useState<SettingsData>(loadSettings)

    // Apply theme on mount and when it changes
    useEffect(() => {
        applyTheme(settings.themeName)
    }, [settings.themeName])

    // Sync favicon with logo
    useEffect(() => {
        // Remove existing favicon link to force browser refresh
        const existing = document.querySelector("link[rel='icon']")
        if (existing) existing.remove()

        const link = document.createElement("link")
        link.rel = "icon"

        if (settings.logo) {
            link.href = settings.logo
            // Extract MIME type from data URL (e.g. "data:image/png;base64,...")
            const match = settings.logo.match(/^data:(image\/[^;]+)/)
            link.type = match ? match[1] : "image/png"
        } else {
            link.href = "/vite.svg"
            link.type = "image/svg+xml"
        }

        document.head.appendChild(link)
    }, [settings.logo])

    const update = useCallback((partial: Partial<SettingsData>) => {
        setSettings((prev) => {
            const next = { ...prev, ...partial }
            persistSettings(next)
            return next
        })
    }, [])

    const setLogo = useCallback((logo: string) => update({ logo }), [update])
    const setThemeName = useCallback((themeName: string) => update({ themeName }), [update])
    const setOrgAddress = useCallback((orgAddress: string) => update({ orgAddress }), [update])

    return (
        <SettingsContext.Provider
            value={{
                ...settings,
                setLogo,
                setThemeName,
                setOrgAddress,
            }}
        >
            {children}
        </SettingsContext.Provider>
    )
}

export function useSettings() {
    const ctx = useContext(SettingsContext)
    if (!ctx) throw new Error("useSettings must be used within SettingsProvider")
    return ctx
}
