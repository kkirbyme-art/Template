import { createRootRoute, Outlet } from "@tanstack/react-router"
import { AuthProvider } from "@/lib/auth"
import { SettingsProvider } from "@/lib/settings"
import { TooltipProvider } from "@/components/ui/tooltip"

export const Route = createRootRoute({
    component: RootComponent,
})

function RootComponent() {
    return (
        <AuthProvider>
            <SettingsProvider>
                <TooltipProvider>
                    <Outlet />
                </TooltipProvider>
            </SettingsProvider>
        </AuthProvider>
    )
}
