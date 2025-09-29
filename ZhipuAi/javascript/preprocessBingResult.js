let c=document.querySelector("#b_results");
document.body.innerHTML = c.innerHTML;

let m=document.getElementsByClassName("b_msg b_canvas");
if(m.length>0){
    for(let i=0;i<m.length;i++){
        m[i].remove();
    }
}

let m2=document.getElementsByClassName("b_pag");
if(m2.length>0){
    for(let i=0;i<m2.length;i++){
        m2[i].remove();
    }
}