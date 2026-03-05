import { createFileRoute, useNavigate } from "@tanstack/react-router"
import { useState } from "react"
import { useAuth } from "@/lib/auth"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"

const CREDENTIALS = {
    user: "reykirbylumanta@gmail.com",
    password: "123",
    id: 69,
    fullname: "Rey Kirby Lumanta",
}

export const Route = createFileRoute("/login")({
    component: LoginPage,
})

function LoginPage() {
    const { login } = useAuth()
    const navigate = useNavigate()
    const [email, setEmail] = useState("")
    const [password, setPassword] = useState("")
    const [error, setError] = useState<string | null>(null)

    function onSubmit(e: React.FormEvent) {
        e.preventDefault()
        setError(null)

        if (email === CREDENTIALS.user && password === CREDENTIALS.password) {
            login({
                id: CREDENTIALS.id,
                user: CREDENTIALS.user,
                fullname: CREDENTIALS.fullname,
            })
            navigate({ to: "/" })
        } else {
            setError("Invalid email or password")
        }
    }

    return (
        <div className="flex min-h-svh w-full items-center justify-center p-6 md:p-10">
            <div className="w-full max-w-sm">
                <Card>
                    <CardHeader>
                        <CardTitle className="text-2xl">Login</CardTitle>
                        <CardDescription>
                            Enter your email below to login to your account
                        </CardDescription>
                    </CardHeader>
                    <CardContent>
                        <form onSubmit={onSubmit}>
                            <div className="flex flex-col gap-6">
                                <div className="grid gap-2">
                                    <Label htmlFor="email">Email</Label>
                                    <Input
                                        id="email"
                                        type="email"
                                        placeholder="m@example.com"
                                        value={email}
                                        onChange={(e) => setEmail(e.target.value)}
                                        required
                                    />
                                </div>
                                <div className="grid gap-2">
                                    <Label htmlFor="password">Password</Label>
                                    <Input
                                        id="password"
                                        type="password"
                                        value={password}
                                        onChange={(e) => setPassword(e.target.value)}
                                        required
                                    />
                                </div>
                                {error && (
                                    <p className="text-sm text-destructive">{error}</p>
                                )}
                                <Button type="submit" className="w-full">
                                    Login
                                </Button>
                            </div>
                            <div className="mt-4 text-center text-sm text-muted-foreground">
                                Use: reykirbylumanta@gmail.com / 123
                            </div>
                        </form>
                    </CardContent>
                </Card>
            </div>
        </div>
    )
}
