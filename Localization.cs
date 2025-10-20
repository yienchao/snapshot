using System.Collections.Generic;

namespace ViewTracker
{
    /// <summary>
    /// Provides localized strings based on Revit's language setting.
    /// Supports English (default) and French.
    /// </summary>
    public static class Localization
    {
        private static string _currentLanguage = "en"; // Default to English

        /// <summary>
        /// Initialize the localization system with the current Revit language.
        /// Call this once during application startup.
        /// </summary>
        /// <param name="revitLanguageCode">The Revit language code (e.g., "FRA", "ENU", "DEU")</param>
        public static void Initialize(string revitLanguageCode)
        {
            // Map Revit language codes to our language codes
            // Revit uses 3-letter codes: ENU = English, FRA = French, DEU = German, etc.
            if (revitLanguageCode.StartsWith("FR", System.StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = "fr";
            }
            else
            {
                _currentLanguage = "en"; // Default to English for all other languages
            }
        }

        /// <summary>
        /// Get a localized string by key.
        /// </summary>
        public static string Get(string key)
        {
            if (_currentLanguage == "fr" && _frenchStrings.TryGetValue(key, out string frenchValue))
                return frenchValue;

            // Fallback to English
            if (_englishStrings.TryGetValue(key, out string englishValue))
                return englishValue;

            // If key not found, return the key itself as fallback
            return $"[{key}]";
        }

        // Shorthand property for common strings
        public static class Common
        {
            public static string Error => Get("Common.Error");
            public static string Success => Get("Common.Success");
            public static string Warning => Get("Common.Warning");
            public static string Cancel => Get("Common.Cancel");
            public static string OK => Get("Common.OK");
            public static string Continue => Get("Common.Continue");
            public static string Close => Get("Common.Close");
            public static string Yes => Get("Common.Yes");
            public static string No => Get("Common.No");
            public static string SelectAll => Get("Common.SelectAll");
            public static string SelectNone => Get("Common.SelectNone");
        }

        #region English Strings
        private static readonly Dictionary<string, string> _englishStrings = new Dictionary<string, string>
        {
            // Common
            { "Common.Error", "Error" },
            { "Common.Success", "Success" },
            { "Common.Warning", "Warning" },
            { "Common.Cancel", "Cancel" },
            { "Common.OK", "OK" },
            { "Common.Continue", "Continue" },
            { "Common.Close", "Close" },
            { "Common.Yes", "Yes" },
            { "Common.No", "No" },
            { "Common.SelectAll", "Select All" },
            { "Common.SelectNone", "Select None" },

            // Validation
            { "Validation.NoProjectID", "This file does not have a valid projectID parameter." },
            { "Validation.DuplicateTrackIDs", "Found duplicate trackIDs in this file:" },
            { "Validation.FixBeforeSnapshot", "Please fix before creating snapshot." },
            { "Validation.InvalidVersionName", "Version name must contain only letters, numbers, underscores, and dashes." },

            // Version
            { "Version.SelectSnapshotType", "Select snapshot type:" },
            { "Version.OfficialDescription", "Official versions should be created by BIM Manager for milestones.\nDraft versions are for work-in-progress tracking." },
            { "Version.DraftVersion", "Draft Version" },
            { "Version.DraftDescription", "For testing and work-in-progress" },
            { "Version.OfficialVersion", "Official Version" },
            { "Version.OfficialDescription2", "For milestones and deliverables (BIM Manager)" },
            { "Version.AlreadyExists", "Version Already Exists" },
            { "Version.ExistsMessage", "Version '{0}' already exists:\n\nType: {1}\nCreated by: {2}\nDate: {3:yyyy-MM-dd HH:mm}\n\nPlease choose a different version name." },
            { "Version.InvalidVersionName", "Invalid Version Name" },

            // Room Snapshot
            { "RoomSnapshot.Title", "Create Room Snapshot" },
            { "RoomSnapshot.NoRooms", "No Rooms" },
            { "RoomSnapshot.NoRoomsMessage", "No rooms found with trackID parameter." },
            { "RoomSnapshot.DuplicateTrackIDs", "Duplicate trackIDs" },
            { "RoomSnapshot.EnterOfficialName", "Enter official version name:\n(e.g., permit_set, design_v2)" },
            { "RoomSnapshot.EnterDraftName", "Enter draft version name:\n(e.g., wip_jan15, test_v1)" },
            { "RoomSnapshot.VersionTitle", "Room Snapshot Version" },
            { "RoomSnapshot.FailedCheckVersions", "Failed to check existing versions:" },
            { "RoomSnapshot.SuccessMessage", "Captured {0} room(s) to database.\n\nVersion: {1} ({2})\nCreated by: {3}\nDate: {4:yyyy-MM-dd HH:mm:ss}" },
            { "RoomSnapshot.FailedUpload", "Failed to upload snapshots:" },

            // Door Snapshot
            { "DoorSnapshot.Title", "Create Door Snapshot" },
            { "DoorSnapshot.NoDoors", "No Doors" },
            { "DoorSnapshot.NoDoorsMessage", "No doors found with trackID parameter." },
            { "DoorSnapshot.EnterOfficialName", "Enter official version name:\n(e.g., permit_set, design_v2)" },
            { "DoorSnapshot.EnterDraftName", "Enter draft version name:\n(e.g., wip_jan15, test_v1)" },
            { "DoorSnapshot.VersionTitle", "Door Snapshot Version" },
            { "DoorSnapshot.SuccessMessage", "Captured {0} door(s) to database.\n\nVersion: {1} ({2})\nCreated by: {3}\nDate: {4:yyyy-MM-dd HH:mm:ss}" },

            // Element Snapshot
            { "ElementSnapshot.Title", "Create Element Snapshot" },
            { "ElementSnapshot.NoElements", "No Elements" },
            { "ElementSnapshot.NoElementsMessage", "No elements found with trackID parameter.\n\nMake sure you've added the 'trackID' shared parameter to the categories you want to track." },
            { "ElementSnapshot.EnterOfficialName", "Enter official version name:\n(e.g., permit_set, design_v2)" },
            { "ElementSnapshot.EnterDraftName", "Enter draft version name:\n(e.g., wip_jan15, test_v1)" },
            { "ElementSnapshot.VersionTitle", "Element Snapshot Version" },
            { "ElementSnapshot.SuccessMessage", "Captured {0} element(s) to database.\n\nCategories:\n{1}\n\nVersion: {2} ({3})\nCreated by: {4}\nDate: {5:yyyy-MM-dd HH:mm:ss}" },

            // Restore
            { "Restore.Title", "Restore Rooms from Snapshot" },
            { "Restore.SelectVersion", "Select a version and parameters to restore" },
            { "Restore.VersionLabel", "Select Version" },
            { "Restore.ScopeLabel", "Scope" },
            { "Restore.OptionsLabel", "Options" },
            { "Restore.AllRooms", "All rooms with trackID" },
            { "Restore.PreSelectedOnly", "Pre-selected rooms only" },
            { "Restore.DeletedOnly", "Deleted rooms only (recreate)" },
            { "Restore.UnplacedOnly", "Unplaced rooms only (restore placement)" },
            { "Restore.SelectParameters", "Select Parameters to Restore" },
            { "Restore.ReadOnlyNote", "Read-only parameters are automatically skipped" },
            { "Restore.PreviewChanges", "Preview Changes..." },
            { "Restore.Restore", "Restore" },

            // Comparison
            { "Compare.Title", "Choose Comparison Mode" },
            { "Compare.SelectWhat", "Select what you want to compare" },
            { "Compare.CurrentVsSnapshot", "Current Model vs Snapshot" },
            { "Compare.CurrentVsSnapshotDesc", "Compare current rooms with a saved snapshot version" },
            { "Compare.SnapshotVsSnapshot", "Snapshot vs Snapshot" },
            { "Compare.SnapshotVsSnapshotDesc", "Compare two different snapshot versions" },
            { "Compare.SelectVersionTitle", "Select Version to Compare" },
            { "Compare.ChooseSnapshot", "Choose a snapshot version to compare with current rooms:" },
            { "Compare.NoVersions", "No Versions Found" },
            { "Compare.NoVersionsMessage", "No snapshot versions found for this project.\n\nPlease create a snapshot first using the Snapshot button." },
            { "Compare.NoChanges", "No Changes" },
            { "Compare.NoChangesMessage", "No differences found between current state and snapshot version." },

            // Ribbon - Versions Panel
            { "Ribbon.Snapshot", "Snapshot" },
            { "Ribbon.Compare", "Compare" },
            { "Ribbon.History", "History" },
            { "Ribbon.Restore", "Restore" },
            { "Ribbon.SnapshotTooltip", "Create snapshot with trackID" },
            { "Ribbon.CompareTooltip", "Compare current state with a snapshot version" },
            { "Ribbon.HistoryTooltip", "View history of all snapshots" },
            { "Ribbon.RestoreTooltip", "Restore parameters from a snapshot version" },

            // Ribbon - View Analytics Panel
            { "Ribbon.InitializeViews", "Initialize\nViews" },
            { "Ribbon.InitializeViewsTooltip", "Initialize all views in the project database" },
            { "Ribbon.ExportCSV", "Export Views\nCSV" },
            { "Ribbon.ExportCSVTooltip", "Export Supabase view_activations for this project's projectID to CSV" },

            // Ribbon - Program Panel
            { "Ribbon.Template", "Template" },
            { "Ribbon.TemplateTooltip", "Download Excel template for space program" },
            { "Ribbon.ImportProgram", "Import\nProgram" },
            { "Ribbon.ImportProgramTooltip", "Import space program from Excel (creates filled regions)" },
            { "Ribbon.ConvertToRooms", "Convert to\nRooms" },
            { "Ribbon.ConvertToRoomsTooltip", "Convert selected filled regions to rooms" },

            // Type labels
            { "Type.Official", "Official" },
            { "Type.Draft", "Draft" },

            // Entity Types (ComboBox)
            { "EntityType.Rooms", "Rooms" },
            { "EntityType.Doors", "Doors" },
            { "EntityType.Elements", "Elements" },

            // Panel Names
            { "Panel.ViewAnalytics", "View Analytics" },
            { "Panel.Program", "Program" },
            { "Panel.Versions", "Versions" },
        };
        #endregion

        #region French Strings
        private static readonly Dictionary<string, string> _frenchStrings = new Dictionary<string, string>
        {
            // Common
            { "Common.Error", "Erreur" },
            { "Common.Success", "Succès" },
            { "Common.Warning", "Avertissement" },
            { "Common.Cancel", "Annuler" },
            { "Common.OK", "OK" },
            { "Common.Continue", "Continuer" },
            { "Common.Close", "Fermer" },
            { "Common.Yes", "Oui" },
            { "Common.No", "Non" },
            { "Common.SelectAll", "Tout sélectionner" },
            { "Common.SelectNone", "Tout désélectionner" },

            // Validation
            { "Validation.NoProjectID", "Ce fichier n'a pas de paramètre projectID valide." },
            { "Validation.DuplicateTrackIDs", "trackID en double trouvés dans ce fichier :" },
            { "Validation.FixBeforeSnapshot", "Veuillez corriger avant de créer un instantané." },
            { "Validation.InvalidVersionName", "Le nom de version doit contenir uniquement des lettres, des chiffres, des traits de soulignement et des tirets." },

            // Version
            { "Version.SelectSnapshotType", "Sélectionnez le type d'instantané :" },
            { "Version.OfficialDescription", "Les versions officielles doivent être créées par le gestionnaire BIM pour les jalons.\nLes versions brouillon sont pour le suivi du travail en cours." },
            { "Version.DraftVersion", "Version brouillon" },
            { "Version.DraftDescription", "Pour les tests et le travail en cours" },
            { "Version.OfficialVersion", "Version officielle" },
            { "Version.OfficialDescription2", "Pour les jalons et les livrables (gestionnaire BIM)" },
            { "Version.AlreadyExists", "La version existe déjà" },
            { "Version.ExistsMessage", "La version '{0}' existe déjà :\n\nType : {1}\nCréé par : {2}\nDate : {3:yyyy-MM-dd HH:mm}\n\nVeuillez choisir un nom de version différent." },
            { "Version.InvalidVersionName", "Nom de version invalide" },

            // Room Snapshot
            { "RoomSnapshot.Title", "Créer un instantané de pièces" },
            { "RoomSnapshot.NoRooms", "Aucune pièce" },
            { "RoomSnapshot.NoRoomsMessage", "Aucune pièce trouvée avec le paramètre trackID." },
            { "RoomSnapshot.DuplicateTrackIDs", "trackID en double" },
            { "RoomSnapshot.EnterOfficialName", "Entrez le nom de la version officielle :\n(ex : jeu_permis, conception_v2)" },
            { "RoomSnapshot.EnterDraftName", "Entrez le nom de la version brouillon :\n(ex : encours_jan15, test_v1)" },
            { "RoomSnapshot.VersionTitle", "Version d'instantané de pièces" },
            { "RoomSnapshot.FailedCheckVersions", "Échec de la vérification des versions existantes :" },
            { "RoomSnapshot.SuccessMessage", "{0} pièce(s) capturée(s) dans la base de données.\n\nVersion : {1} ({2})\nCréé par : {3}\nDate : {4:yyyy-MM-dd HH:mm:ss}" },
            { "RoomSnapshot.FailedUpload", "Échec du téléchargement des instantanés :" },

            // Door Snapshot
            { "DoorSnapshot.Title", "Créer un instantané de portes" },
            { "DoorSnapshot.NoDoors", "Aucune porte" },
            { "DoorSnapshot.NoDoorsMessage", "Aucune porte trouvée avec le paramètre trackID." },
            { "DoorSnapshot.EnterOfficialName", "Entrez le nom de la version officielle :\n(ex : jeu_permis, conception_v2)" },
            { "DoorSnapshot.EnterDraftName", "Entrez le nom de la version brouillon :\n(ex : encours_jan15, test_v1)" },
            { "DoorSnapshot.VersionTitle", "Version d'instantané de portes" },
            { "DoorSnapshot.SuccessMessage", "{0} porte(s) capturée(s) dans la base de données.\n\nVersion : {1} ({2})\nCréé par : {3}\nDate : {4:yyyy-MM-dd HH:mm:ss}" },

            // Element Snapshot
            { "ElementSnapshot.Title", "Créer un instantané d'éléments" },
            { "ElementSnapshot.NoElements", "Aucun élément" },
            { "ElementSnapshot.NoElementsMessage", "Aucun élément trouvé avec le paramètre trackID.\n\nAssurez-vous d'avoir ajouté le paramètre partagé 'trackID' aux catégories que vous souhaitez suivre." },
            { "ElementSnapshot.EnterOfficialName", "Entrez le nom de la version officielle :\n(ex : jeu_permis, conception_v2)" },
            { "ElementSnapshot.EnterDraftName", "Entrez le nom de la version brouillon :\n(ex : encours_jan15, test_v1)" },
            { "ElementSnapshot.VersionTitle", "Version d'instantané d'éléments" },
            { "ElementSnapshot.SuccessMessage", "{0} élément(s) capturé(s) dans la base de données.\n\nCatégories :\n{1}\n\nVersion : {2} ({3})\nCréé par : {4}\nDate : {5:yyyy-MM-dd HH:mm:ss}" },

            // Restore
            { "Restore.Title", "Restaurer les pièces depuis un instantané" },
            { "Restore.SelectVersion", "Sélectionnez une version et les paramètres à restaurer" },
            { "Restore.VersionLabel", "Sélectionner la version" },
            { "Restore.ScopeLabel", "Portée" },
            { "Restore.OptionsLabel", "Options" },
            { "Restore.AllRooms", "Toutes les pièces avec trackID" },
            { "Restore.PreSelectedOnly", "Pièces présélectionnées uniquement" },
            { "Restore.DeletedOnly", "Pièces supprimées uniquement (recréer)" },
            { "Restore.UnplacedOnly", "Pièces non placées uniquement (restaurer l'emplacement)" },
            { "Restore.SelectParameters", "Sélectionner les paramètres à restaurer" },
            { "Restore.ReadOnlyNote", "Les paramètres en lecture seule sont automatiquement ignorés" },
            { "Restore.PreviewChanges", "Aperçu des modifications..." },
            { "Restore.Restore", "Restaurer" },

            // Comparison
            { "Compare.Title", "Choisir le mode de comparaison" },
            { "Compare.SelectWhat", "Sélectionnez ce que vous voulez comparer" },
            { "Compare.CurrentVsSnapshot", "Modèle actuel vs Instantané" },
            { "Compare.CurrentVsSnapshotDesc", "Comparer les pièces actuelles avec une version d'instantané enregistrée" },
            { "Compare.SnapshotVsSnapshot", "Instantané vs Instantané" },
            { "Compare.SnapshotVsSnapshotDesc", "Comparer deux versions d'instantané différentes" },
            { "Compare.SelectVersionTitle", "Sélectionner la version à comparer" },
            { "Compare.ChooseSnapshot", "Choisissez une version d'instantané à comparer avec les pièces actuelles :" },
            { "Compare.NoVersions", "Aucune version trouvée" },
            { "Compare.NoVersionsMessage", "Aucune version d'instantané trouvée pour ce projet.\n\nVeuillez d'abord créer un instantané en utilisant le bouton Instantané." },
            { "Compare.NoChanges", "Aucun changement" },
            { "Compare.NoChangesMessage", "Aucune différence trouvée entre l'état actuel et la version d'instantané." },

            // Ribbon - Versions Panel
            { "Ribbon.Snapshot", "Instantané" },
            { "Ribbon.Compare", "Comparer" },
            { "Ribbon.History", "Historique" },
            { "Ribbon.Restore", "Restaurer" },
            { "Ribbon.SnapshotTooltip", "Créer un instantané avec trackID" },
            { "Ribbon.CompareTooltip", "Comparer l'état actuel avec une version d'instantané" },
            { "Ribbon.HistoryTooltip", "Voir l'historique de tous les instantanés" },
            { "Ribbon.RestoreTooltip", "Restaurer les paramètres depuis une version d'instantané" },

            // Ribbon - View Analytics Panel
            { "Ribbon.InitializeViews", "Initialiser\nVues" },
            { "Ribbon.InitializeViewsTooltip", "Initialiser toutes les vues dans la base de données du projet" },
            { "Ribbon.ExportCSV", "Exporter Vues\nCSV" },
            { "Ribbon.ExportCSVTooltip", "Exporter les activations de vues Supabase pour le projectID de ce projet vers CSV" },

            // Ribbon - Program Panel
            { "Ribbon.Template", "Modèle" },
            { "Ribbon.TemplateTooltip", "Télécharger le modèle Excel pour le programme d'espaces" },
            { "Ribbon.ImportProgram", "Importer\nProgramme" },
            { "Ribbon.ImportProgramTooltip", "Importer le programme d'espaces depuis Excel (crée des régions remplies)" },
            { "Ribbon.ConvertToRooms", "Convertir en\nPièces" },
            { "Ribbon.ConvertToRoomsTooltip", "Convertir les régions remplies sélectionnées en pièces" },

            // Type labels
            { "Type.Official", "Officielle" },
            { "Type.Draft", "Brouillon" },

            // Entity Types (ComboBox)
            { "EntityType.Rooms", "Pièces" },
            { "EntityType.Doors", "Portes" },
            { "EntityType.Elements", "Éléments" },

            // Panel Names
            { "Panel.ViewAnalytics", "Analyse des vues" },
            { "Panel.Program", "Programme" },
            { "Panel.Versions", "Versions" },
        };
        #endregion
    }
}
