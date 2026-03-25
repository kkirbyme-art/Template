import { createFileRoute, useNavigate } from "@tanstack/react-router"
import { useAuth } from "@/lib/auth"
import { useSettings, themePresets } from "@/lib/settings"
import { useRef } from "react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
} from "@/components/ui/card"
import { Separator } from "@/components/ui/separator"
import { IconUpload, IconTrash } from "@tabler/icons-react"

export const Route = createFileRoute("/_authenticated/settings/general")({
    component: GeneralSettingsPage,
})

function GeneralSettingsPage() {
    const { user } = useAuth()
    const navigate = useNavigate()
    const {
        logo,
        themeName,
        orgAddress,
        setLogo,
        setThemeName,
        setOrgAddress,
    } = useSettings()
    const fileInputRef = useRef<HTMLInputElement>(null)

    // Guard: only super admin
    if (user?.user_level !== 696969) {
        navigate({ to: "/" })
        return null
    }

    function handleLogoUpload(e: React.ChangeEvent<HTMLInputElement>) {
        const file = e.target.files?.[0]
        if (!file) return
        if (!file.type.startsWith("image/")) {
            alert("Please select an image file.")
            return
        }
        if (file.size > 2_000_000) {
            alert("Image must be smaller than 2MB.")
            return
        }

        const reader = new FileReader()
        reader.onload = () => {
            if (typeof reader.result === "string") {
                setLogo(reader.result)
            }
            // Reset so re-uploading the same file triggers onChange
            if (fileInputRef.current) fileInputRef.current.value = ""
        }
        reader.readAsDataURL(file)
    }

    return (
        <div className="space-y-6">
            <div>
                <h2 className="text-2xl font-bold tracking-tight">
                    General Settings
                </h2>
                <p className="text-muted-foreground">
                    Manage application branding and appearance.
                </p>
            </div>
            <Separator />

            {/* Logo Upload */}
            <Card>
                <CardHeader>
                    <CardTitle>Organization Logo</CardTitle>
                    <CardDescription>
                        Upload your organization logo. It will appear in the sidebar and
                        browser tab. Max 2MB, image files only.
                    </CardDescription>
                </CardHeader>
                <CardContent className="space-y-4">
                    <div className="flex items-center gap-4">
                        <div className="flex size-16 items-center justify-center rounded-lg border bg-muted">
                            {logo ? (
                                <img
                                    src={logo}
                                    alt="Logo"
                                    className="size-12 object-contain"
                                />
                            ) : (
                                <span className="text-xs text-muted-foreground">No logo</span>
                            )}
                        </div>
                        <div className="flex gap-2">
                            <input
                                ref={fileInputRef}
                                type="file"
                                accept="image/*"
                                className="hidden"
                                onChange={handleLogoUpload}
                            />
                            <Button
                                variant="outline"
                                size="sm"
                                onClick={() => fileInputRef.current?.click()}
                            >
                                <IconUpload className="mr-2 size-4" />
                                Upload
                            </Button>
                            {logo && (
                                <Button
                                    variant="outline"
                                    size="sm"
                                    onClick={() => setLogo("")}
                                >
                                    <IconTrash className="mr-2 size-4" />
                                    Remove
                                </Button>
                            )}
                        </div>
                    </div>
                </CardContent>
            </Card>

            {/* Theme Color */}
            <Card>
                <CardHeader>
                    <CardTitle>Theme Color</CardTitle>
                    <CardDescription>
                        Choose the primary accent color for the application.
                    </CardDescription>
                </CardHeader>
                <CardContent>
                    <div className="grid grid-cols-5 gap-3 sm:grid-cols-10">
                        {themePresets.map((preset) => (
                            <button
                                key={preset.name}
                                onClick={() => setThemeName(preset.name)}
                                className={
                                    "group flex flex-col items-center gap-1.5 rounded-lg p-2 transition-colors hover:bg-accent" +
                                    (themeName === preset.name
                                        ? " bg-accent ring-2 ring-primary ring-offset-2"
                                        : "")
                                }
                            >
                                <div
                                    className="size-8 rounded-full border shadow-sm"
                                    style={{ background: preset.primary }}
                                />
                                <span className="text-[10px] font-medium">
                                    {preset.label}
                                </span>
                            </button>
                        ))}
                    </div>
                </CardContent>
            </Card>

            {/* Organization Address */}
            <Card>
                <CardHeader>
                    <CardTitle>Organization Address</CardTitle>
                    <CardDescription>
                        Set the organization address. This can be accessed throughout the
                        application.
                    </CardDescription>
                </CardHeader>
                <CardContent className="space-y-2">
                    <Label htmlFor="org-address">Address</Label>
                    <Input
                        id="org-address"
                        placeholder="Enter organization address"
                        value={orgAddress}
                        onChange={(e) => setOrgAddress(e.target.value)}
                    />
                </CardContent>
            </Card>
        </div>
    )
}
