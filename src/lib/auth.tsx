import { createContext, useContext, useState, type ReactNode } from "react"

interface User {
    id: number
    user: string
    fullname: string
}

interface AuthContextType {
    user: User | null
    login: (user: User) => void
    logout: () => void
}

const AuthContext = createContext<AuthContextType | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<User | null>(() => {
        const stored = sessionStorage.getItem("auth_user")
        return stored ? JSON.parse(stored) : null
    })

    function login(u: User) {
        sessionStorage.setItem("auth_user", JSON.stringify(u))
        setUser(u)
    }

    function logout() {
        sessionStorage.removeItem("auth_user")
        setUser(null)
    }

    return (
        <AuthContext.Provider value={{ user, login, logout }}>
            {children}
        </AuthContext.Provider>
    )
}

export function useAuth() {
    const ctx = useContext(AuthContext)
    if (!ctx) throw new Error("useAuth must be used within AuthProvider")
    return ctx
}
