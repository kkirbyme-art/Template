import { createRootRoute, Outlet } from "@tanstack/react-router"
import { AuthProvider } from "@/lib/auth"
import { TooltipProvider } from "@/components/ui/tooltip"

export const Route = createRootRoute({
    component: RootComponent,
})

function RootComponent() {
    return (
        <AuthProvider>
            <TooltipProvider>
                <Outlet />
            </TooltipProvider>
        </AuthProvider>
    )
}
