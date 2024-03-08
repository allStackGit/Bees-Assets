


using Assets.Scripts.Scenes;
using Assets.Scripts.Server;

namespace Assets.Scripts.Settings
{
    public class ServerSettings
    {
        /* Base class for server settings. Every class follows a standard structure:
         * 1. Name - acts as the server key for getting the needed settings from the server
         * 2. Fetch - Grabs the data from the server and loads it into the properties, gets data specific to user if the UserId is other than zero
         * 3. Instantiation: Calls the fetch method to load the properties
         */
        public string Name;
        public bool IsLoaded;
        public int UserId = 0;
        private SettingsRequest _request = null;
        private Scene _scene;

        public ServerSettings(string name, int userId, Scene scene)
        {
            Name = name;
            UserId = userId;
            _scene = scene;
            Fetch();
        }
        protected virtual void ProcessData(string contents)
        {

        }
        public void WaitForResponse()
        {
            if (_request != null)
            {
                SettingsRequest standingRequest = (SettingsRequest)_scene.Socket.GetStandingRequest(_request.Hash);
                if (standingRequest.Status == 1)
                {
                    //Debug.Log($"The standing request has completed, setting the contents: {standingRequest.Response.Contents}");
                    IsLoaded = true;
                    ProcessData(standingRequest.Response.Contents);
                }
            }
        }

        protected virtual void Fetch()
        {
            //Debug.Log("Fetching data for Server Settings");
            _request = new SettingsRequest(new GetUserSettingsData(ConfigData.GetUserId(), Name, ConfigData.Version),
                this, ConfigData.StandardMaxTimeOnQueue);
            _scene.Socket.SendRequest(_request);
        }
    }
}