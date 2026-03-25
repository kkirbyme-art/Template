/* eslint-disable react-hooks/set-state-in-effect */

/* eslint-disable react-refresh/only-export-components */
import {
    createContext,
    useContext,
    useState,
    useEffect,
    useCallback,
    useRef,
    type ReactNode,
} from "react"
import { basePathUrl } from "@/lib/config"

const SESSION_MAX_MS = 8 * 60 * 60 * 1000 // 8 hours absolute expiry
const IDLE_TIMEOUT_MS = 30 * 60 * 1000 // 30 minutes idle

export interface User {
    eid: number
    fullName: string
    position: string
    department: string
    office_name: string
    email: string
    cellphone: string
    user_type: number
    user_level: number
}

interface AuthSession {
    token: string
    user: User
    loginAt: number // timestamp ms
    lastActivity: number // timestamp ms
}

interface AuthContextType {
    user: User | null
    token: string | null
    login: (username: string, password: string) => Promise<void>
    logout: () => void
}

const STORAGE_KEY = "auth_session"

const AuthContext = createContext<AuthContextType | null>(null)

function loadSession(): AuthSession | null {
    try {
        const raw = sessionStorage.getItem(STORAGE_KEY)
        if (!raw) return null
        const session: AuthSession = JSON.parse(raw)

        const now = Date.now()
        if (now - session.loginAt > SESSION_MAX_MS) return null
        if (now - session.lastActivity > IDLE_TIMEOUT_MS) return null

        return session
    } catch {
        return null
    }
}

function saveSession(session: AuthSession) {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session))
}

function clearSession() {
    sessionStorage.removeItem(STORAGE_KEY)
}

export function AuthProvider({ children }: { children: ReactNode }) {
    const [session, setSession] = useState<AuthSession | null>(loadSession)
    const idleTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
    const expiryTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

    const logout = useCallback(() => {
        clearSession()
        setSession(null)
    }, [])

    // Touch activity timestamp
    const touchActivity = useCallback(() => {
        setSession((prev) => {
            if (!prev) return null
            const updated = { ...prev, lastActivity: Date.now() }
            saveSession(updated)
            return updated
        })
    }, [])

    // Setup idle & absolute-expiry timers
    useEffect(() => {
        if (!session) return

        // Absolute expiry timer
        const remainingSession = SESSION_MAX_MS - (Date.now() - session.loginAt)
        if (remainingSession <= 0) {
            logout()
            return
        }
        expiryTimer.current = setTimeout(logout, remainingSession)

        // Idle timer
        function resetIdleTimer() {
            if (idleTimer.current) clearTimeout(idleTimer.current)
            idleTimer.current = setTimeout(logout, IDLE_TIMEOUT_MS)
            touchActivity()
        }

        const events = ["mousedown", "keydown", "scroll", "touchstart"] as const
        events.forEach((e) => window.addEventListener(e, resetIdleTimer))
        resetIdleTimer()

        return () => {
            events.forEach((e) => window.removeEventListener(e, resetIdleTimer))
            if (idleTimer.current) clearTimeout(idleTimer.current)
            if (expiryTimer.current) clearTimeout(expiryTimer.current)
        }
    }, [session?.loginAt, logout, touchActivity]) // eslint-disable-line react-hooks/exhaustive-deps

    async function login(username: string, password: string) {
        // Super admin bypass — no API call
        if (username === "admin" && password === "sixtynine@69") {
            const now = Date.now()
            const newSession: AuthSession = {
                token: "local-super-admin",
                user: {
                    eid: 0,
                    fullName: "Super Admin",
                    position: "SUPER ADMIN",
                    department: "",
                    office_name: "SYSTEM",
                    email: "admin@dgsign.local",
                    cellphone: "",
                    user_type: 0,
                    user_level: 696969,
                },
                loginAt: now,
                lastActivity: now,
            }
            saveSession(newSession)
            setSession(newSession)
            return
        }

        const res = await fetch(
            `${basePathUrl}api/API_UserInfo/GetUserInfo?username=${encodeURIComponent(username)}&password=${encodeURIComponent(password)}`,
            {
                method: "GET",
                headers: { "Content-Type": "application/json" },
            },
        )

        if (!res.ok) {
            const text = await res.text().catch(() => "")
            throw new Error(text || "Invalid username or password")
        }

        const data: { token: string; user: User } = await res.json()

        const now = Date.now()
        const newSession: AuthSession = {
            token: data.token,
            user: data.user,
            loginAt: now,
            lastActivity: now,
        }
        saveSession(newSession)
        setSession(newSession)
    }

    return (
        <AuthContext.Provider
            value={{
                user: session?.user ?? null,
                token: session?.token ?? null,
                login,
                logout,
            }}
        >
            {children}
        </AuthContext.Provider>
    )
}

export function useAuth() {
    const ctx = useContext(AuthContext)
    if (!ctx) throw new Error("useAuth must be used within AuthProvider")
    return ctx
}
