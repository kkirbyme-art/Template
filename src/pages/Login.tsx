import { Button } from "@/components/ui/button"
import { useState } from "react"

const CREDENTIALS = {
    user: "reykirbylumanta@gmail.com",
    password: "123",
    id: 69,
    fullname: "Rey Kirby Lumanta",
}
export default function Login({ onSuccess }: { onSuccess?: () => void }) {
    const [username, setUsername] = useState("")
    const [password, setPassword] = useState("")
    const [error, setError] = useState<string | null>(null)

    function onSubmit(e: React.FormEvent) {
        e.preventDefault()
        setError(null)

        if (username === CREDENTIALS.user && password === CREDENTIALS.password) {
            onSuccess?.()
        } else {
            setError("Invalid username or password")
        }
    }

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
            <div className="absolute inset-0 bg-black/40" />

            <div className="relative w-full max-w-md rounded-lg bg-background p-6 shadow-lg">
                <h2 className="mb-4 text-2xl font-semibold">Sign in</h2>

                <form onSubmit={onSubmit} className="space-y-4">
                    <div>
                        <label className="mb-1 block text-sm font-medium">Username</label>
                        <input
                            type="text"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            className="w-full rounded-md border px-3 py-2"
                            placeholder="admin"
                            required
                        />
                    </div>

                    <div>
                        <label className="mb-1 block text-sm font-medium">Password</label>
                        <input
                            type="password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className="w-full rounded-md border px-3 py-2"
                            placeholder="admin1"
                            required
                        />
                    </div>

                    {error && <p className="text-sm text-destructive">{error}</p>}

                    <div className="flex justify-between items-center">
                        <div className="text-sm text-muted-foreground">Use admin / admin1</div>
                        <Button type="submit">Sign in</Button>
                    </div>
                </form>
            </div>
        </div>
    )
}
