mergeInto(LibraryManager.library, {

    Test: function (){
        window.alert('Test?');
    },

    WebSocketInit: function(url, protocol) {
        this.socket = new WebSocket(url, protocol);
    },
    WebSocketSend: function(message) {
        this.socket.send(message);
    },

    WebSocketAddEventListener: function (type, method) {
        this.socket.addEventListener(type, (event) => {
            method(event);
        });
    },

});
